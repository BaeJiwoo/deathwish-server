using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using DeathWish.Protocol;
using DeathWish.Server.Physics;

namespace DeathWish.Server;

public sealed partial class GameWorld
{
    private const double SNAPSHOT_INTERVAL_SECONDS = 0.1;
    private const float FLOOR_SIDE_LENGTH_METERS = 100f;
    private const int SPAWN_MIN_X_METERS = -7;
    private const int SPAWN_MAX_X_METERS = 24;
    private const int SPAWN_MIN_ABS_Z_METERS = 2;
    private const int SPAWN_MAX_ABS_Z_METERS = 24;
    internal const float MOVEMENT_SPEED_METERS_PER_SECOND = 5f;
    internal const int DEFAULT_ADMISSION_CAPACITY = 1024;
    internal const int DEFAULT_MAX_COMMANDS_PER_TICK = 128;

    // Keep the spawn strips clear of the ramp and ring. Prefer the floor nearest the origin.
    private static readonly (int X, int Z)[] SpawnCells =
        (from z in Enumerable.Range(-SPAWN_MAX_ABS_Z_METERS, SPAWN_MAX_ABS_Z_METERS * 2 + 1)
         from x in Enumerable.Range(SPAWN_MIN_X_METERS, SPAWN_MAX_X_METERS - SPAWN_MIN_X_METERS + 1)
         where Math.Abs(z) >= SPAWN_MIN_ABS_Z_METERS
         orderby (x + 1) * (x + 1) + (z - SPAWN_MIN_ABS_Z_METERS) * (z - SPAWN_MIN_ABS_Z_METERS)
         select (x, z)).ToArray();

    private readonly Dictionary<WorldAdmission, WorldPlayer> _players = new();
    private readonly IReadOnlyList<MapSpawn> _arenaSpawns;
    private PlayerPhysicsSimulation? _physics;
    private readonly int _maxCommandsPerTick;
    private readonly float _gravityMetersPerSecondSquared;
    private PlayerState[] _publishedPlayers = Array.Empty<PlayerState>();
    private long _lastPlayerId;
    private long _currentTick;
    private bool _stopped;
    private double _snapshotElapsedSeconds;

    public int RoomId { get; }
    internal WorldMapId MapId { get; }
    /// <summary>Safe to read from the admission thread. Accepted reservations remain valid after this changes.</summary>
    internal bool IsAcceptingMatchPlayers => _arenaPhase != MatchPhase.Finished;
    public long CurrentTick => Interlocked.Read(ref _currentTick);
    public int PlayerCount => Volatile.Read(ref _publishedPlayers).Length;
    internal WorldCommandQueue Commands { get; }

    /// <summary>World-owned colliders. After startup only the owning world loop may access them.</summary>
    internal IReadOnlyList<Collider> Colliders { get; }

    internal GameWorld(int roomId, int admissionCapacity = DEFAULT_ADMISSION_CAPACITY,
        int maxCommandsPerTick = DEFAULT_MAX_COMMANDS_PER_TICK, TimeProvider? timeProvider = null,
        float gravityMetersPerSecondSquared = ServerOptions.DEFAULT_GRAVITY_METERS_PER_SECOND_SQUARED,
        WorldMapId mapId = WorldMapId.Flat, int teamDeathmatchTeamSize = ServerOptions.DEFAULT_TDM_TEAM_SIZE,
        bool isTrainingRoom = false)
    {
        if (roomId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(roomId));
        }
        if (maxCommandsPerTick < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCommandsPerTick));
        }
        if (teamDeathmatchTeamSize is < 1 or > MatchState.MAX_TEAM_SIZE)
        {
            throw new ArgumentOutOfRangeException(nameof(teamDeathmatchTeamSize));
        }
        if (isTrainingRoom && mapId != WorldMapId.TeamDeathmatch)
        {
            throw new ArgumentException("Training uses the team deathmatch arena.", nameof(mapId));
        }

        RoomId = roomId;
        MapId = mapId;
        _maxCommandsPerTick = maxCommandsPerTick;
        _gravityMetersPerSecondSquared = gravityMetersPerSecondSquared;
        _arenaTeamSize = isTrainingRoom ? MatchState.MAX_TEAM_SIZE : teamDeathmatchTeamSize;
        _isTrainingRoom = isTrainingRoom;

        if (mapId == WorldMapId.Flat)
        {
            Colliders = [new PlaneCollider(Vector3.Zero, FLOOR_SIDE_LENGTH_METERS, FLOOR_SIDE_LENGTH_METERS)];
            _arenaSpawns = [];
        }
        else
        {
            MeshWorldMap map = MeshWorldMap.Load(mapId);
            Colliders = map.Colliders;
            _arenaSpawns = map.Spawns;
        }

        bool isArena = mapId == WorldMapId.TeamDeathmatch;
        int capacity = isArena ? Math.Min(admissionCapacity, 2 * _arenaTeamSize) : admissionCapacity;
        Commands = new WorldCommandQueue(capacity, timeProvider, isArena);
    }

    /// <summary>Creates a simulation from the room's static geometry and gravity. The caller owns and disposes it.</summary>
    internal PlayerPhysicsSimulation CreatePhysicsSimulation() => new(Colliders, _gravityMetersPerSecondSquared);

    /// <summary>Returns a defensive copy of the player states published after the last completed update.</summary>
    public PlayerState[] GetPlayers() => Volatile.Read(ref _publishedPlayers).ToArray();

    /// <summary>Runs one synchronous update using a finite, positive delta in seconds. Only the owning world loop may call this method.</summary>
    internal void Tick(float deltaTimeSeconds)
    {
        if (!float.IsFinite(deltaTimeSeconds) || deltaTimeSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaTimeSeconds));
        }
        if (_stopped)
        {
            throw new InvalidOperationException("The world has stopped.");
        }
        long completedTick = checked(_currentTick + 1);
        bool isArena = MapId == WorldMapId.TeamDeathmatch;
        if (isArena)
        {
            _arenaTimeSeconds += deltaTimeSeconds;
        }

        bool changed = false;
        for (int count = 0; count < _maxCommandsPerTick && Commands.TryDequeue(out WorldCommand command); count++)
        {
            WorldAdmission admission = command.Admission;
            switch (command.Type)
            {
                case WorldCommandType.Join:
                    if (admission.IsDeparted || admission.Session.IsDisconnected)
                    {
                        break;
                    }
                    long playerId = checked(_lastPlayerId + 1);
                    _physics ??= CreatePhysicsSimulation();
                    WorldPlayer player = CreatePlayer(admission, playerId);
                    _players.Add(admission, player);
                    _lastPlayerId = playerId;
                    changed = true;
                    Vector3 spawn = player.State.Position;
                    var ack = new JoinAck(RoomId, playerId, completedTick, spawn.X, spawn.Y, spawn.Z, MapId);
                    admission.Session.Send(PacketCodec.EncodeJoinAck(ack));
                    break;
                case WorldCommandType.Leave:
                    if (_players.Remove(admission, out WorldPlayer? leaving))
                    {
                        if (leaving.Body is BodyHandle body)
                        {
                            _physics!.RemovePlayer(body);
                        }
                        _isArenaStateDirty |= leaving.Arena is not null;
                        if (isArena && _players.Count == 0)
                        {
                            ResetArena();
                        }
                        changed = true;
                    }
                    Commands.Release(admission);
                    break;
                default:
                    throw new InvalidOperationException("Unknown world command.");
            }
        }

        if (isArena)
        {
            TryStartArena();
        }

        // Lifecycle commands run first; movement never adds commands to the bounded Join/Leave queue.
        changed |= StepPhysics(deltaTimeSeconds, completedTick);
        if (changed)
        {
            Volatile.Write(ref _publishedPlayers, _players.Values.Select(player => player.State).ToArray());
        }

        if (isArena)
        {
            PublishArenaUpdate(completedTick);
        }

        _snapshotElapsedSeconds += deltaTimeSeconds;
        if (_hasArenaRespawned || _snapshotElapsedSeconds >= SNAPSHOT_INTERVAL_SECONDS)
        {
            _snapshotElapsedSeconds %= SNAPSHOT_INTERVAL_SECONDS;
            BroadcastSnapshot(completedTick);
            _hasArenaRespawned = false;
        }

        // Publish completion after simulation work; readers do not acquire ownership of world state.
        Interlocked.Exchange(ref _currentTick, completedTick);
    }

    private WorldPlayer CreatePlayer(WorldAdmission admission, long playerId)
    {
        if (MapId == WorldMapId.TeamDeathmatch)
        {
            return CreateArenaPlayer(admission, playerId);
        }

        Vector3 spawn = FindGridSpawnPosition();
        BodyHandle body = _physics!.AddPlayer(spawn);
        return new WorldPlayer(body, new PlayerState(playerId, admission.Session.SessionId, spawn));
    }

    private static bool IsOverlappingPlayer(Vector3 spawnPosition, Vector3 playerPosition)
    {
        Vector3 offset = Vector3.Abs(spawnPosition - playerPosition);
        return offset.X < PlayerPhysicsSimulation.PLAYER_WIDTH_METERS &&
            offset.Y < PlayerPhysicsSimulation.PLAYER_HEIGHT_METERS &&
            offset.Z < PlayerPhysicsSimulation.PLAYER_DEPTH_METERS;
    }

    private Vector3 FindGridSpawnPosition()
    {
        // Block the integer grid points that would overlap a standing player's footprint.
        var occupied = new HashSet<(int X, int Z)>();
        var nearestCells = new HashSet<(int X, int Z)>();
        foreach (WorldPlayer player in _players.Values)
        {
            Vector3 foot = player.State.Position;
            bool outsideSpawnArea = MathF.Abs(foot.Y) >= PlayerPhysicsSimulation.PLAYER_HEIGHT_METERS ||
                foot.X < SPAWN_MIN_X_METERS - 1 || foot.X > SPAWN_MAX_X_METERS + 1 ||
                MathF.Abs(foot.Z) > SPAWN_MAX_ABS_Z_METERS + 1;
            if (outsideSpawnArea)
            {
                continue;
            }
            nearestCells.Add(((int)MathF.Round(foot.X), (int)MathF.Round(foot.Z)));
            int maxX = (int)MathF.Floor(foot.X + PlayerPhysicsSimulation.PLAYER_WIDTH_METERS);
            int maxZ = (int)MathF.Floor(foot.Z + PlayerPhysicsSimulation.PLAYER_DEPTH_METERS);
            for (int x = (int)MathF.Ceiling(foot.X - PlayerPhysicsSimulation.PLAYER_WIDTH_METERS); x <= maxX; x++)
            {
                for (int z = (int)MathF.Ceiling(foot.Z - PlayerPhysicsSimulation.PLAYER_DEPTH_METERS); z <= maxZ; z++)
                {
                    occupied.Add((x, z));
                }
            }
        }
        if (!occupied.Contains((0, 0)))
        {
            return Vector3.Zero;
        }

        Vector3? crowdedFallback = null;
        foreach ((int x, int z) in SpawnCells)
        {
            if (!occupied.Contains((x, z)))
            {
                return new Vector3(x, 0, z);
            }
            if (crowdedFallback == null && !nearestCells.Contains((x, z)))
            {
                crowdedFallback = new Vector3(x, 0, z);
            }
        }
        // The strips contain more grid cells than the room's admission capacity.
        return crowdedFallback ?? throw new InvalidOperationException("Spawn area exhausted.");
    }

    private void BroadcastSnapshot(long serverTick)
    {
        var players = _players.Where(pair => !pair.Key.IsDeparted && !pair.Key.Session.IsDisconnected)
            .Select(pair => pair.Value.State)
            .Select(state => new SnapshotPlayer(state.PlayerId, state.Position.X, state.Position.Y, state.Position.Z))
            .OrderBy(player => player.PlayerId).ToArray();
        byte[] frame = SnapshotCodec.Encode(new WorldSnapshot(RoomId, serverTick, players));
        foreach (WorldAdmission admission in _players.Keys)
        {
            if (!admission.IsDeparted && !admission.Session.IsDisconnected)
            {
                admission.Session.SendSnapshot(frame);
            }
        }
    }

    private bool StepPhysics(float deltaTimeSeconds, long serverTick)
    {
        if (_physics is null || _players.Count == 0)
        {
            return false;
        }

        bool changed = false;
        foreach ((WorldAdmission admission, WorldPlayer player) in _players)
        {
            if (admission.IsDeparted || admission.Session.IsDisconnected)
            {
                continue;
            }

            if (player.Body is not BodyHandle body)
            {
                changed |= TryRespawnArenaPlayer(player);
                ReplyToDiscardedInputs(admission);
                continue;
            }
            if (player.Arena is not null && _arenaPhase != MatchPhase.Playing)
            {
                _physics.SetHorizontalVelocity(body, 0f, 0f);
                ReplyToDiscardedInputs(admission);
                continue;
            }

            Vector3 velocity = admission.Movement.GetDirection() * MOVEMENT_SPEED_METERS_PER_SECOND;
            _physics.SetHorizontalVelocity(body, velocity.X, velocity.Z);
            ApplyPlayerAction(admission, player, body, deltaTimeSeconds, serverTick);
        }

        _physics.Step(deltaTimeSeconds);
        foreach ((WorldAdmission admission, WorldPlayer player) in _players)
        {
            if (admission.IsDeparted || admission.Session.IsDisconnected || player.Body is not BodyHandle body)
            {
                continue;
            }

            Vector3 position = _physics.GetMotion(body).FootPositionMeters;
            if (position != player.State.Position)
            {
                player.State = player.State with { Position = position };
                changed = true;
            }
        }
        return changed;
    }

    private void ApplyPlayerAction(WorldAdmission admission, WorldPlayer player, BodyHandle body, float deltaTimeSeconds,
        long serverTick)
    {
        PlayerPhysicsSimulation physics = _physics!;
        if (player.IsJumping && physics.GetMotion(body).VerticalVelocityMetersPerSecond <= 0 &&
            physics.HasGroundSupport(body))
        {
            player.IsJumping = false;
        }
        if (!admission.Movement.TryTakeAction(out CameraRay? ray))
        {
            return;
        }

        if (ray.HasValue)
        {
            if (player.Arena is not null)
            {
                ApplyArenaShot(admission.Session, player, body, ray.Value, serverTick);
            }
            else
            {
                SendRaycastResult(admission.Session, physics.CastCameraRay(body, ray.Value));
            }
        }
        else if (!player.IsJumping && _gravityMetersPerSecondSquared > 0 && physics.HasGroundSupport(body))
        {
            physics.ApplyJumpImpulse(body, _gravityMetersPerSecondSquared, deltaTimeSeconds);
            player.IsJumping = true;
        }
    }

    private void SendRaycastResult(ClientSession session, (CollidableReference? Target, float DistanceMeters, Vector3 PointMeters) result)
    {
        var (target, distanceMeters, pointMeters) = result;
        if (target is not CollidableReference hit)
        {
            session.Send(RaycastMissFrame);
            return;
        }

        string name = hit.Mobility == CollidableMobility.Static
            ? "terrain"
            : $"player {_players.Values.First(other => other.Body == hit.BodyHandle).State.PlayerId}";
        string message = FormattableString.Invariant(
            $"Raycast hit {name} at ({pointMeters.X:F2}, {pointMeters.Y:F2}, {pointMeters.Z:F2}), {distanceMeters:F2} m");
        session.Send(ActionCodec.EncodeResult(message));
    }

    // Only the world loop performs terminal cleanup, after its last update or on failure.
    internal void Shutdown()
    {
        Commands.Complete();
        _players.Clear();
        _physics?.Dispose();
        _physics = null;
        Volatile.Write(ref _publishedPlayers, Array.Empty<PlayerState>());
        _stopped = true;
    }

    private sealed class WorldPlayer(BodyHandle? body, PlayerState state, ArenaParticipant? arena = null)
    {
        internal BodyHandle? Body { get; set; } = body;
        internal PlayerState State { get; set; } = state;
        internal ArenaParticipant? Arena { get; } = arena;
        internal bool IsJumping { get; set; }
    }
}
