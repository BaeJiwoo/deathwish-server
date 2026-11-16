using System.Net;

namespace DeathWish.Server;

public sealed class ServerOptions
{
    public const string DefaultAddress = "127.0.0.1";
    public const int DefaultPort = 7777;
    public const int DefaultRoomCount = 2;
    public const int DefaultTickRate = 30;

    public string Address { get; }
    public int Port { get; }
    public int RoomCount { get; }
    public int TickRate { get; }

    public ServerOptions(string address = DefaultAddress, int port = DefaultPort,
        int roomCount = DefaultRoomCount, int tickRate = DefaultTickRate)
    {
        if (!IPAddress.TryParse(address, out _)) throw new ArgumentException("Expected an IP address.", nameof(address));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (roomCount < 1) throw new ArgumentOutOfRangeException(nameof(roomCount));
        if (tickRate < 1) throw new ArgumentOutOfRangeException(nameof(tickRate));

        Address = address;
        Port = port;
        RoomCount = roomCount;
        TickRate = tickRate;
    }
}
