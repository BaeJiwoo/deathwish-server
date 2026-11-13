using DotNetty.Transport.Channels;

namespace DeathWish.Server;

public sealed class ClientSession
{
    private int _disconnected;

    public Guid SessionId { get; } = Guid.NewGuid();
    public IChannel Channel { get; }
    public bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;

    internal ClientSession(IChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        Channel = channel;
    }

    // ChannelInactive owns the terminal transition; repeated notifications must not repeat cleanup.
    internal bool TryMarkDisconnected() => Interlocked.Exchange(ref _disconnected, 1) == 0;
}
