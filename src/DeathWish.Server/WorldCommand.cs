namespace DeathWish.Server;

internal enum WorldCommandType { Join, Leave }

internal readonly record struct WorldCommand(WorldCommandType Type, WorldAdmission Admission);

internal sealed class WorldAdmission(ClientSession session, TimeProvider? timeProvider = null, string? nickname = null,
    bool isArena = false, bool receiveArenaShots = false)
{
    private int _departed;

    internal ClientSession Session { get; } = session;
    internal string? Nickname { get; } = nickname;
    internal bool ReceivesArenaShots { get; } = isArena && receiveArenaShots;
    internal MovementMailbox Movement { get; } = new(timeProvider ?? TimeProvider.System);
    internal MatchLatency? Latency { get; } = isArena ? new MatchLatency(timeProvider ?? TimeProvider.System) : null;
    internal bool IsDeparted => Volatile.Read(ref _departed) != 0;

    // This token identifies one admission, so an old departure cannot affect a later rejoin.
    internal void MarkDeparted()
    {
        Volatile.Write(ref _departed, 1);
        Movement.Close();
    }
}
