using System.Net;
using DeathWish.Protocol;

namespace DeathWish.Server;

public sealed class ServerOptions
{
    public const string DefaultAddress = "127.0.0.1";
    public const int DefaultPort = 7777;
    public const int DefaultRoomCount = 2;
    public const int DefaultTickRate = 30;
    public const float DEFAULT_GRAVITY_METERS_PER_SECOND_SQUARED = 9.81f;
    public const int DEFAULT_TDM_TEAM_SIZE = MatchState.MAX_TEAM_SIZE;

    public string Address { get; }
    public int Port { get; }
    public int RoomCount { get; }
    public int TickRate { get; }
    public float GravityMetersPerSecondSquared { get; }
    public int? TeamDeathmatchRoomId { get; }
    public int TeamDeathmatchTeamSize { get; }
    public int? TrainingRoomId { get; }

    public ServerOptions(string address = DefaultAddress, int port = DefaultPort,
        int roomCount = DefaultRoomCount, int tickRate = DefaultTickRate,
        float gravityMetersPerSecondSquared = DEFAULT_GRAVITY_METERS_PER_SECOND_SQUARED,
        int? teamDeathmatchRoomId = null, int teamDeathmatchTeamSize = DEFAULT_TDM_TEAM_SIZE, int? trainingRoomId = null)
    {
        if (!IPAddress.TryParse(address, out _)) throw new ArgumentException("Expected an IP address.", nameof(address));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (roomCount < 1) throw new ArgumentOutOfRangeException(nameof(roomCount));
        if (tickRate < 1) throw new ArgumentOutOfRangeException(nameof(tickRate));

        if (!float.IsFinite(gravityMetersPerSecondSquared) || gravityMetersPerSecondSquared < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gravityMetersPerSecondSquared));
        }
        ValidateArenaRoomIds(roomCount, teamDeathmatchRoomId, trainingRoomId);
        if (teamDeathmatchTeamSize is < 1 or > MatchState.MAX_TEAM_SIZE)
        {
            throw new ArgumentOutOfRangeException(nameof(teamDeathmatchTeamSize));
        }

        Address = address;
        Port = port;
        RoomCount = roomCount;
        TickRate = tickRate;
        GravityMetersPerSecondSquared = gravityMetersPerSecondSquared;
        TeamDeathmatchRoomId = teamDeathmatchRoomId;
        TeamDeathmatchTeamSize = teamDeathmatchTeamSize;
        TrainingRoomId = trainingRoomId;
    }

    internal static void ValidateArenaRoomIds(int roomCount, int? teamDeathmatchRoomId, int? trainingRoomId)
    {
        if (teamDeathmatchRoomId is int matchId && (matchId < 1 || matchId > roomCount))
        {
            throw new ArgumentOutOfRangeException(nameof(teamDeathmatchRoomId));
        }
        if (trainingRoomId is not int trainingId)
        {
            return;
        }
        if (trainingId < 1 || trainingId > roomCount)
        {
            throw new ArgumentOutOfRangeException(nameof(trainingRoomId));
        }
        if (trainingId == teamDeathmatchRoomId)
        {
            throw new ArgumentException("SERVER_TRAINING_ROOM_ID and SERVER_TDM_ROOM_ID must identify different rooms.",
                nameof(trainingRoomId));
        }
    }
}
