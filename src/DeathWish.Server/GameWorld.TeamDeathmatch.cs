using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using DeathWish.Protocol;
using DeathWish.Server.Physics;

namespace DeathWish.Server;

public sealed partial class GameWorld
{
    private const int ARENA_SHOT_DAMAGE = 25;
    private const double ARENA_SHOT_INTERVAL_SECONDS = 0.2;
    private const double ARENA_RESPAWN_DELAY_SECONDS = 3.0;
    private const int ARENA_SCORE_LIMIT = 10;
    private const double ARENA_ASSIST_WINDOW_SECONDS = 5.0;

    private static readonly byte[] RaycastMissFrame = ActionCodec.EncodeResult("Raycast miss");

    private readonly int _arenaTeamSize;
    private readonly bool _isTrainingRoom;
    private volatile MatchPhase _arenaPhase;
    private TeamId? _arenaWinner;
    private int _arenaRedScore;
    private int _arenaBlueScore;
    private double _arenaTimeSeconds;
    private bool _isArenaStateDirty;
    private bool _hasArenaRespawned;

    private WorldPlayer CreateArenaPlayer(WorldAdmission admission, long playerId)
    {
        TeamId team = PickArenaTeam();
        string nickname = admission.Nickname ?? throw new InvalidOperationException("Arena admission requires a nickname.");
        var arena = new ArenaParticipant(nickname, team);
        Vector3? spawn = FindArenaSpawn(team);
        BodyHandle? body = null;
        if (spawn is Vector3 position)
        {
            body = _physics!.AddPlayer(position);
            arena.Health = MatchState.MAX_HEALTH;
        }

        // If opponents block every marker, publish a bodyless admission and retry on later ticks.
        Vector3 foot = spawn ?? _arenaSpawns.First(marker => marker.TeamId == (int)team).Position;
        _isArenaStateDirty = true;
        return new WorldPlayer(body, new PlayerState(playerId, admission.Session.SessionId, foot), arena);
    }

    private TeamId PickArenaTeam()
    {
        int red = _players.Values.Count(player => player.Arena?.Team == TeamId.Red);
        int blue = _players.Count - red;
        return red <= blue ? TeamId.Red : TeamId.Blue;
    }

    private void TryStartArena()
    {
        int requiredPlayerCount = _isTrainingRoom ? 1 : 2 * _arenaTeamSize;
        if (_arenaPhase != MatchPhase.Waiting || _players.Count < requiredPlayerCount)
        {
            return;
        }

        int readyRed = 0;
        int readyBlue = 0;
        foreach ((WorldAdmission admission, WorldPlayer player) in _players)
        {
            if (!IsActiveAdmission(admission))
            {
                continue;
            }
            if (player.Arena!.Team == TeamId.Red)
            {
                readyRed++;
            }
            else
            {
                readyBlue++;
            }
        }
        bool isReady = _isTrainingRoom
            ? readyRed + readyBlue > 0
            : readyRed == _arenaTeamSize && readyBlue == _arenaTeamSize;
        if (!isReady)
        {
            return;
        }

        _arenaPhase = MatchPhase.Playing;
        // Discard inputs sent before the ready state is published.
        foreach (WorldAdmission admission in _players.Keys)
        {
            ReplyToDiscardedInputs(admission);
        }
        _isArenaStateDirty = true;
    }

    private void ResetArena()
    {
        _arenaRedScore = 0;
        _arenaBlueScore = 0;
        _arenaWinner = null;
        _arenaPhase = MatchPhase.Waiting;
    }

    private Vector3? FindArenaSpawn(TeamId team)
    {
        foreach (MapSpawn marker in _arenaSpawns)
        {
            if (marker.TeamId != (int)team)
            {
                continue;
            }

            bool isOccupied = _players.Values.Any(player =>
                player.Body.HasValue && IsOverlappingPlayer(marker.Position, player.State.Position));
            if (!isOccupied)
            {
                return marker.Position;
            }
        }
        return null;
    }

    private bool TryRespawnArenaPlayer(WorldPlayer player)
    {
        ArenaParticipant arena = player.Arena!;
        if (_arenaPhase == MatchPhase.Finished || _arenaTimeSeconds < arena.RespawnAtSeconds ||
            FindArenaSpawn(arena.Team) is not Vector3 spawn)
        {
            return false;
        }

        player.Body = _physics!.AddPlayer(spawn);
        player.State = player.State with { Position = spawn };
        player.IsJumping = false;
        arena.Health = MatchState.MAX_HEALTH;
        _isArenaStateDirty = true;
        _hasArenaRespawned = true;
        return true;
    }

    private static void ReplyToDiscardedInputs(WorldAdmission admission)
    {
        int discardedRays = admission.Movement.DiscardPendingInputs();
        for (int index = 0; index < discardedRays; index++)
        {
            admission.Session.Send(RaycastMissFrame);
        }
    }

    private void ApplyArenaShot(ClientSession session, WorldPlayer attacker, BodyHandle body, CameraRay ray, long serverTick)
    {
        ArenaParticipant arena = attacker.Arena!;
        if (_arenaTimeSeconds < arena.NextShotAtSeconds)
        {
            session.Send(RaycastMissFrame);
            return;
        }
        arena.NextShotAtSeconds = _arenaTimeSeconds + ARENA_SHOT_INTERVAL_SECONDS;

        PlayerPhysicsSimulation.ArenaShotTrace trace = _physics!.CastArenaShot(body, ray);
        SendRaycastResult(session, (trace.Target, trace.DistanceMeters, trace.EndMeters));
        ArenaShotOutcome outcome = ResolveArenaShot(attacker, trace.Target, serverTick);
        var shot = new ArenaShot(RoomId, serverTick, attacker.State.PlayerId, trace.OriginMeters, trace.EndMeters, outcome);
        BroadcastArenaShot(shot);
    }

    private ArenaShotOutcome ResolveArenaShot(WorldPlayer attacker, CollidableReference? hit, long serverTick)
    {
        if (hit is not CollidableReference target)
        {
            return ArenaShotOutcome.Miss;
        }
        if (target.Mobility == CollidableMobility.Static)
        {
            return ArenaShotOutcome.Terrain;
        }

        foreach ((WorldAdmission admission, WorldPlayer victim) in _players)
        {
            if (victim.Body == target.BodyHandle && IsActiveAdmission(admission) && attacker.Arena!.Team != victim.Arena!.Team)
            {
                ApplyArenaDamage(attacker, victim, target.BodyHandle, serverTick);
                return ArenaShotOutcome.Damage;
            }
        }
        return ArenaShotOutcome.PlayerContact;
    }

    private void BroadcastArenaShot(ArenaShot shot)
    {
        byte[]? frame = null;
        foreach (WorldAdmission admission in _players.Keys)
        {
            if (admission.ReceivesArenaShots && IsActiveAdmission(admission))
            {
                admission.Session.Send(frame ??= MatchCodec.EncodeShot(shot));
            }
        }
    }

    private void ApplyArenaDamage(WorldPlayer attacker, WorldPlayer victim, BodyHandle body, long serverTick)
    {
        ArenaParticipant attacking = attacker.Arena!;
        ArenaParticipant defending = victim.Arena!;
        defending.Health = Math.Max(0, defending.Health - ARENA_SHOT_DAMAGE);
        defending.RecentDamageBy[attacker.State.PlayerId] = _arenaTimeSeconds;
        _isArenaStateDirty = true;
        if (defending.Health > 0)
        {
            return;
        }

        defending.Deaths++;
        attacking.Kills++;
        AwardArenaAssists(attacker, defending);
        defending.RespawnAtSeconds = _arenaTimeSeconds + ARENA_RESPAWN_DELAY_SECONDS;
        _physics!.RemovePlayer(body);
        victim.Body = null;
        ScoreArenaKill(attacking.Team);

        var kill = new KillEvent(RoomId, serverTick, attacker.State.PlayerId, attacking.Nickname,
            victim.State.PlayerId, defending.Nickname);
        byte[] frame = MatchCodec.EncodeKill(kill);
        foreach (WorldAdmission admission in _players.Keys)
        {
            if (IsActiveAdmission(admission))
            {
                admission.Session.Send(frame);
            }
        }
    }

    private void AwardArenaAssists(WorldPlayer killer, ArenaParticipant victim)
    {
        foreach ((WorldAdmission admission, WorldPlayer contributor) in _players)
        {
            if (!ReferenceEquals(killer, contributor) && IsActiveAdmission(admission) &&
                victim.RecentDamageBy.TryGetValue(contributor.State.PlayerId, out double lastDamageSeconds) &&
                _arenaTimeSeconds - lastDamageSeconds <= ARENA_ASSIST_WINDOW_SECONDS)
            {
                contributor.Arena!.Assists++;
            }
        }
        victim.RecentDamageBy.Clear();
    }

    private void ScoreArenaKill(TeamId team)
    {
        int score = team == TeamId.Red ? ++_arenaRedScore : ++_arenaBlueScore;
        if (_isTrainingRoom || score < ARENA_SCORE_LIMIT)
        {
            return;
        }

        _arenaWinner = team;
        _arenaPhase = MatchPhase.Finished;
        foreach (WorldPlayer player in _players.Values)
        {
            if (player.Body is BodyHandle body)
            {
                _physics!.SetHorizontalVelocity(body, 0f, 0f);
            }
        }
    }

    private void PublishArenaUpdate(long serverTick)
    {
        foreach ((WorldAdmission admission, WorldPlayer player) in _players)
        {
            _isArenaStateDirty |= player.Arena!.PublishedPingMs != admission.Latency!.RoundTripMs;
        }
        if (_isArenaStateDirty)
        {
            BroadcastArenaState(serverTick);
            _isArenaStateDirty = false;
        }

        // Reliable output order keeps the first probe after the admission response and match roster.
        foreach (WorldAdmission admission in _players.Keys)
        {
            if (IsActiveAdmission(admission) && admission.Latency!.PollProbe() is long token)
            {
                admission.Session.Send(PingCodec.EncodeProbe(token));
            }
        }
    }

    private void BroadcastArenaState(long serverTick)
    {
        var recipients = _players.Where(pair => IsActiveAdmission(pair.Key))
            .OrderBy(pair => pair.Value.State.PlayerId).ToArray();
        if (recipients.Length == 0)
        {
            return;
        }

        var players = new MatchPlayer[recipients.Length];
        for (int index = 0; index < recipients.Length; index++)
        {
            WorldPlayer player = recipients[index].Value;
            ArenaParticipant arena = player.Arena!;
            arena.PublishedPingMs = recipients[index].Key.Latency!.RoundTripMs;
            players[index] = new MatchPlayer(player.State.PlayerId, arena.Nickname, arena.Team, arena.Health,
                arena.Kills, arena.Deaths, arena.Assists, arena.PublishedPingMs);
        }

        var match = new MatchState(RoomId, serverTick, _arenaPhase, _arenaRedScore, _arenaBlueScore, _arenaWinner, players);
        byte[] frame = MatchCodec.EncodeState(match);
        foreach (var recipient in recipients)
        {
            recipient.Key.Session.Send(frame);
        }
    }

    private static bool IsActiveAdmission(WorldAdmission admission) =>
        !admission.IsDeparted && !admission.Session.IsDisconnected;

    private sealed class ArenaParticipant(string nickname, TeamId team)
    {
        internal Dictionary<long, double> RecentDamageBy { get; } = new();
        internal string Nickname { get; } = nickname;
        internal TeamId Team { get; } = team;
        internal int Health { get; set; }
        internal int Kills { get; set; }
        internal int Deaths { get; set; }
        internal int Assists { get; set; }
        internal int PublishedPingMs { get; set; } = MatchState.UNKNOWN_PING_MS;
        internal double NextShotAtSeconds { get; set; }
        internal double RespawnAtSeconds { get; set; }
    }
}
