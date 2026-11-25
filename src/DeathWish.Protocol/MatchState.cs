using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;

namespace DeathWish.Protocol;

public enum TeamId : byte
{
    Red = 1,
    Blue = 2
}

public enum MatchPhase : byte
{
    Waiting = 0,
    Playing = 1,
    Finished = 2
}

public readonly struct JoinMatchRequest
{
    public int RoomId { get; }
    public string Nickname { get; }
    public bool ReceiveArenaShots { get; }

    public JoinMatchRequest(int roomId, string nickname)
        : this(roomId, nickname, false)
    {
    }

    public JoinMatchRequest(int roomId, string nickname, bool receiveArenaShots)
    {
        RoomId = roomId;
        Nickname = nickname;
        ReceiveArenaShots = receiveArenaShots;
    }
}

public readonly struct MatchPlayer
{
    public long PlayerId { get; }
    public string Nickname { get; }
    public TeamId Team { get; }
    public int Health { get; }
    public int Kills { get; }
    public int Deaths { get; }
    public int Assists { get; }
    public int PingMs { get; }

    public MatchPlayer(long playerId, string nickname, TeamId team, int health, int kills, int deaths, int assists, int pingMs)
    {
        PlayerId = playerId;
        Nickname = nickname;
        Team = team;
        Health = health;
        Kills = kills;
        Deaths = deaths;
        Assists = assists;
        PingMs = pingMs;
    }
}

public sealed class MatchState
{
    public const int MAX_HEALTH = 100;
    public const int MAX_TEAM_SIZE = 5;
    public const int MAX_PLAYERS = 2 * MAX_TEAM_SIZE;
    public const int UNKNOWN_PING_MS = -1;

    public int RoomId { get; }
    public long ServerTick { get; }
    public MatchPhase Phase { get; }
    public int RedScore { get; }
    public int BlueScore { get; }
    public TeamId? Winner { get; }
    public ReadOnlyCollection<MatchPlayer> Players { get; }

    /// <summary>Takes a copy of the roster. The caller retains ownership of the supplied list.</summary>
    public MatchState(int roomId, long serverTick, MatchPhase phase, int redScore, int blueScore, TeamId? winner,
        IReadOnlyList<MatchPlayer> players)
    {
        if (players is null)
        {
            throw new ArgumentNullException(nameof(players));
        }

        RoomId = roomId;
        ServerTick = serverTick;
        Phase = phase;
        RedScore = redScore;
        BlueScore = blueScore;
        Winner = winner;

        var copy = new MatchPlayer[players.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = players[index];
        }
        Players = Array.AsReadOnly(copy);
    }
}

public readonly struct KillEvent
{
    public int RoomId { get; }
    public long ServerTick { get; }
    public long AttackerId { get; }
    public string AttackerNickname { get; }
    public long VictimId { get; }
    public string VictimNickname { get; }

    public KillEvent(int roomId, long serverTick, long attackerId, string attackerNickname, long victimId,
        string victimNickname)
    {
        RoomId = roomId;
        ServerTick = serverTick;
        AttackerId = attackerId;
        AttackerNickname = attackerNickname;
        VictimId = victimId;
        VictimNickname = victimNickname;
    }
}

public enum ArenaShotOutcome : byte
{
    Miss = 0,
    Terrain = 1,
    PlayerContact = 2,
    Damage = 3
}

/// <summary>An accepted arena shot with server-simulated world coordinates in meters.</summary>
public readonly struct ArenaShot
{
    public int RoomId { get; }
    public long ServerTick { get; }
    public long ShooterPlayerId { get; }
    public Vector3 OriginMeters { get; }
    public Vector3 EndMeters { get; }
    public ArenaShotOutcome Outcome { get; }

    public ArenaShot(int roomId, long serverTick, long shooterPlayerId, Vector3 originMeters, Vector3 endMeters,
        ArenaShotOutcome outcome)
    {
        RoomId = roomId;
        ServerTick = serverTick;
        ShooterPlayerId = shooterPlayerId;
        OriginMeters = originMeters;
        EndMeters = endMeters;
        Outcome = outcome;
    }
}
