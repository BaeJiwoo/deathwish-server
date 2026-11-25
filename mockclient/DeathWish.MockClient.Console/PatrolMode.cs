using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using DeathWish.MockClient.Core;
using DeathWish.Protocol;

internal static class PatrolMode
{
    private const int INPUT_INTERVAL_MS = 33;
    private const int WRITE_TIMEOUT_SECONDS = 5;
    private const double SNAPSHOT_TIMEOUT_SECONDS = 1;
    private const double PATH_RETRY_SECONDS = 2;
    private const double BLOCKED_ROUTE_RETRY_SECONDS = 0.25;
    private const double WAYPOINT_STALL_SECONDS = 4;
    private const double JUMP_TIMEOUT_SECONDS = 3;
    private const double MIN_COMBAT_SHOT_INTERVAL_SECONDS = 0.32;
    private const double COMBAT_SHOT_JITTER_SECONDS = 0.15;
    private const float WALK_RADIUS_METERS = 0.12f;
    private const float JUMP_LANDING_RADIUS_METERS = 0.25f;
    private const float HEIGHT_TOLERANCE_METERS = 0.14f;
    private const float JUMP_SOURCE_TOLERANCE_METERS = 0.035f;
    private const float PROGRESS_DISTANCE_METERS = 0.06f;
    private const float BRAKING_DISTANCE_METERS = 1f;
    private const float AIRBORNE_RISE_METERS = 0.35f;

    private static readonly Lazy<PatrolNavigation> Navigation = new(PatrolNavigation.Load);

    internal static IReadOnlyList<PatrolRoute> Routes => PatrolRoute.All;

    internal static bool TryGetRoute(int number, [NotNullWhen(true)] out PatrolRoute? route) =>
        PatrolRoute.TryGet(number, out route);

    /// <summary>Owns outgoing movement while running. Await it before issuing another request on the same connection.</summary>
    internal static async Task<uint> RunAsync(ConnectionTestClient client, JoinAck admission, PatrolRoute route,
        uint movementSequence, CancellationToken stopToken, CancellationToken connectionToken)
    {
        if (admission.MapId != WorldMapId.TeamDeathmatch)
        {
            throw new ArgumentException("Patrol routes require the TeamDeathmatch arena.", nameof(admission));
        }

        Console.WriteLine($"Patrol {route.Number} {route.Name} active.");
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopToken, connectionToken);
        var clock = Stopwatch.StartNew();
        var progress = new PatrolProgress(route, admission.ServerTick);
        bool hasMovement = false;
        bool hasPendingShot = false;
        try
        {
            while (!stopToken.IsCancellationRequested && !client.Disconnected.IsCompleted)
            {
                connectionToken.ThrowIfCancellationRequested();
                if (hasPendingShot && client.TryReadRaycastResult(out _))
                {
                    hasPendingShot = false;
                }

                PatrolInput input = progress.Read(client, admission, clock.Elapsed.TotalSeconds);
                if (input.Direction != Vector2.Zero || hasMovement || input.ShouldJump)
                {
                    await SendMovementAsync(client, movementSequence, input.Direction, connectionToken);
                    movementSequence = unchecked(movementSequence + 1);
                    hasMovement = input.Direction != Vector2.Zero;
                }
                if (input.ShouldJump && !stopToken.IsCancellationRequested)
                {
                    using var timeout = CreateWriteTimeout(connectionToken);
                    await client.SendJumpAsync(timeout.Token);
                }
                if (!hasPendingShot && input.Shot is CameraRay shot && !stopToken.IsCancellationRequested)
                {
                    using var timeout = CreateWriteTimeout(connectionToken);
                    await client.SendRayAsync(shot, timeout.Token);
                    hasPendingShot = true;
                }

                // Cancel an idle wait when patrol stops. Canceling a Core send itself closes the connection.
                await Task.Delay(INPUT_INTERVAL_MS, delayCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested && !connectionToken.IsCancellationRequested)
        {
        }

        if (!client.Disconnected.IsCompleted && !connectionToken.IsCancellationRequested)
        {
            await SendMovementAsync(client, movementSequence, Vector2.Zero, connectionToken);
            movementSequence = unchecked(movementSequence + 1);
            if (hasPendingShot)
            {
                using var timeout = CreateWriteTimeout(connectionToken);
                // Leave no automatic shot reply for the next manual command.
                while (!client.TryReadRaycastResult(out _) && !client.Disconnected.IsCompleted)
                {
                    await Task.Delay(INPUT_INTERVAL_MS, timeout.Token);
                }
            }
        }
        return movementSequence;
    }

    private static async Task SendMovementAsync(ConnectionTestClient client, uint sequence, Vector2 direction,
        CancellationToken connectionToken)
    {
        using var timeout = CreateWriteTimeout(connectionToken);
        await client.SendMoveAsync(new MoveInput(sequence, direction.X, direction.Y), timeout.Token);
    }

    private static CancellationTokenSource CreateWriteTimeout(CancellationToken connectionToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(WRITE_TIMEOUT_SECONDS));
        return timeout;
    }

    private sealed class PatrolProgress(PatrolRoute route, long startTick)
    {
        private MovePlan? _plan;
        private MatchPhase? _phase;
        private int? _deaths;
        private bool _wasDead;
        private long _minSnapshotTick = startTick;
        private long _lastSnapshotTick;
        private double _lastSnapshotSeconds;
        private double _nextPathRetrySeconds;
        private double _nextShotSeconds;
        private Vector2? _combatPost;

        internal PatrolInput Read(ConnectionTestClient client, JoinAck admission, double nowSeconds)
        {
            MatchState? match = client.LatestMatchState;
            if (match == null)
            {
                return default;
            }
            if (_phase != match.Phase)
            {
                _phase = match.Phase;
                _plan = null;
                _nextPathRetrySeconds = 0;
                if (_phase != MatchPhase.Playing)
                {
                    Console.WriteLine($"Patrol {route.Number} waiting for play.");
                }
            }

            MatchPlayer local = match.Players.FirstOrDefault(player => player.PlayerId == admission.PlayerId);
            if (_phase != MatchPhase.Playing || local.PlayerId == 0)
            {
                return default;
            }
            if (local.Health == 0)
            {
                if (!_wasDead)
                {
                    Console.WriteLine($"Patrol {route.Number} waiting for respawn.");
                }
                _wasDead = true;
                _deaths = local.Deaths;
                _plan = null;
                return default;
            }

            if (_wasDead || _deaths.HasValue && _deaths.Value != local.Deaths)
            {
                Console.WriteLine($"Patrol {route.Number} respawned; returning to route.");
                _minSnapshotTick = match.ServerTick;
                _plan = null;
                _nextPathRetrySeconds = 0;
            }
            _wasDead = false;
            _deaths = local.Deaths;

            WorldSnapshot? snapshot = client.LatestSnapshot;
            if (snapshot == null || snapshot.ServerTick < _minSnapshotTick)
            {
                return default;
            }
            if (snapshot.ServerTick != _lastSnapshotTick)
            {
                _lastSnapshotTick = snapshot.ServerTick;
                _lastSnapshotSeconds = nowSeconds;
            }
            if (nowSeconds - _lastSnapshotSeconds >= SNAPSHOT_TIMEOUT_SECONDS)
            {
                return default;
            }

            SnapshotPlayer observed = snapshot.Players.FirstOrDefault(player => player.PlayerId == admission.PlayerId);
            if (observed.PlayerId == 0)
            {
                return default;
            }
            var foot = new Vector3(observed.X, observed.Y, observed.Z);
            if (route.IsCombatDrill)
            {
                return ReadCombat(match, snapshot, local, foot, nowSeconds);
            }

            if (_plan == null && nowSeconds >= _nextPathRetrySeconds)
            {
                var (entry, circuit) = route.GetCircuit(local.Team);
                SetPath(foot, new Vector2(entry.X, entry.Z), circuit, nowSeconds);
            }
            return FollowPath(foot, snapshot.ServerTick, nowSeconds);
        }

        private PatrolInput ReadCombat(MatchState match, WorldSnapshot snapshot, MatchPlayer local, Vector3 foot,
            double nowSeconds)
        {
            int slot = match.Players.Count(player => player.Team == local.Team && player.PlayerId < local.PlayerId);
            Vector2 post = CombatDrill.GetPost(local.Team, slot);
            if (_combatPost != post)
            {
                _combatPost = post;
                _plan = null;
                _nextPathRetrySeconds = 0;
            }

            var groundPosition = new Vector2(foot.X, foot.Z);
            bool isAtPost = Vector2.Distance(groundPosition, post) <= WALK_RADIUS_METERS &&
                MathF.Abs(foot.Y) <= HEIGHT_TOLERANCE_METERS;
            if (!isAtPost)
            {
                if (_plan == null && nowSeconds >= _nextPathRetrySeconds)
                {
                    SetPath(foot, post, [], nowSeconds);
                }
                return FollowPath(foot, snapshot.ServerTick, nowSeconds);
            }

            _plan = null;
            if (nowSeconds < _nextShotSeconds ||
                CombatDrill.AimAtClosestEnemy(foot, local.Team, match, snapshot) is not CameraRay shot)
            {
                return default;
            }
            _nextShotSeconds = nowSeconds + MIN_COMBAT_SHOT_INTERVAL_SECONDS +
                Random.Shared.NextDouble() * COMBAT_SHOT_JITTER_SECONDS;
            return new PatrolInput(Vector2.Zero, false, shot);
        }

        private void SetPath(Vector3 foot, Vector2 destination, PatrolStep[] circuit, double nowSeconds)
        {
            Vector2[] path = Navigation.Value.FindApproach(new Vector2(foot.X, foot.Z), destination);
            if (path.Length == 0)
            {
                Console.WriteLine($"Patrol {route.Number} cannot reach the route from here; retrying.");
                _nextPathRetrySeconds = nowSeconds + PATH_RETRY_SECONDS;
                return;
            }
            _plan = new MovePlan(route.Number, path, circuit, nowSeconds);
        }

        private PatrolInput FollowPath(Vector3 foot, long snapshotTick, double nowSeconds)
        {
            if (_plan == null)
            {
                return default;
            }

            if (!_plan.TryAdvance(foot, snapshotTick, nowSeconds, out PatrolInput input))
            {
                Console.WriteLine($"Patrol {route.Number} blocked at {_plan.Target}; returning to route.");
                _plan = null;
                _nextPathRetrySeconds = nowSeconds + BLOCKED_ROUTE_RETRY_SECONDS;
            }
            return input;
        }
    }

    private sealed class MovePlan(int routeNumber, Vector2[] approach, PatrolStep[] circuit, double startSeconds)
    {
        private int _approachIndex;
        private int _circuitIndex;
        private float _bestDistanceMeters = float.PositiveInfinity;
        private double _lastProgressSeconds = startSeconds;
        private long _landingTick;
        private long _jumpReadyTick;
        private bool _hasJumped;
        private bool _wasAirborne;

        private bool IsApproaching => _approachIndex < approach.Length;
        private bool HasFinished => !IsApproaching && circuit.Length == 0;

        private PatrolStep Current => IsApproaching
            ? new PatrolStep(new Vector3(approach[_approachIndex].X, 0, approach[_approachIndex].Y))
            : circuit[_circuitIndex];

        internal Vector3 Target => Current.Target;

        internal bool TryAdvance(Vector3 foot, long snapshotTick, double nowSeconds, out PatrolInput input)
        {
            if (HasFinished)
            {
                input = default;
                return true;
            }
            PatrolStep step = Current;
            Vector2 delta = Remaining(foot, step.Target);
            float distance = delta.Length();
            while (HasArrived(foot.Y, distance, step, snapshotTick))
            {
                NextStep(nowSeconds);
                if (HasFinished)
                {
                    input = default;
                    return true;
                }
                step = Current;
                delta = Remaining(foot, step.Target);
                distance = delta.Length();
            }

            bool shouldJump = false;
            if (step.JumpStartHeightMeters is float sourceHeight)
            {
                if (!_hasJumped)
                {
                    shouldJump = IsReadyToJump(foot.Y, sourceHeight, snapshotTick);
                    _hasJumped = shouldJump;
                    if (shouldJump)
                    {
                        _lastProgressSeconds = nowSeconds;
                    }
                }
                if (_hasJumped && nowSeconds - _lastProgressSeconds >= JUMP_TIMEOUT_SECONDS)
                {
                    input = default;
                    return false;
                }
                if (!_hasJumped)
                {
                    input = default;
                    return nowSeconds - _lastProgressSeconds < WAYPOINT_STALL_SECONDS;
                }
            }
            else
            {
                if (distance < _bestDistanceMeters - PROGRESS_DISTANCE_METERS)
                {
                    _bestDistanceMeters = distance;
                    _lastProgressSeconds = nowSeconds;
                }
                if (nowSeconds - _lastProgressSeconds >= WAYPOINT_STALL_SECONDS)
                {
                    input = default;
                    return false;
                }
            }

            Vector2 direction = distance == 0 ? Vector2.Zero : delta / MathF.Max(distance, BRAKING_DISTANCE_METERS);
            input = new PatrolInput(direction, shouldJump);
            return true;
        }

        private bool IsReadyToJump(float height, float sourceHeight, long snapshotTick)
        {
            if (MathF.Abs(height - sourceHeight) > JUMP_SOURCE_TOLERANCE_METERS)
            {
                _jumpReadyTick = 0;
                return false;
            }
            if (_jumpReadyTick == 0)
            {
                // Let the player settle after a ramp seam or a previous landing before queuing a jump.
                _jumpReadyTick = snapshotTick;
                return false;
            }
            return snapshotTick > _jumpReadyTick;
        }

        private bool HasArrived(float height, float distance, PatrolStep step, long snapshotTick)
        {
            bool isAtTargetHeight = MathF.Abs(height - step.Target.Y) <= HEIGHT_TOLERANCE_METERS;
            if (step.JumpStartHeightMeters is not float sourceHeight)
            {
                return isAtTargetHeight && distance <= WALK_RADIUS_METERS;
            }

            _wasAirborne |= _hasJumped && height >= sourceHeight + AIRBORNE_RISE_METERS;
            if (!_wasAirborne || !isAtTargetHeight || distance > JUMP_LANDING_RADIUS_METERS)
            {
                _landingTick = 0;
                return false;
            }
            if (_landingTick == 0)
            {
                _landingTick = snapshotTick;
                return false;
            }
            return snapshotTick > _landingTick;
        }

        private void NextStep(double nowSeconds)
        {
            if (IsApproaching)
            {
                _approachIndex++;
                if (!IsApproaching)
                {
                    Console.WriteLine($"Patrol {routeNumber} reached area.");
                }
            }
            else
            {
                _circuitIndex = (_circuitIndex + 1) % circuit.Length;
            }
            _bestDistanceMeters = float.PositiveInfinity;
            _lastProgressSeconds = nowSeconds;
            _landingTick = 0;
            _jumpReadyTick = 0;
            _hasJumped = false;
            _wasAirborne = false;
        }

        private static Vector2 Remaining(Vector3 foot, Vector3 target) => new(target.X - foot.X, target.Z - foot.Z);
    }

    private readonly record struct PatrolInput(Vector2 Direction, bool ShouldJump, CameraRay? Shot = null);
}
