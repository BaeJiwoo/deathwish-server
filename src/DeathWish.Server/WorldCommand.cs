namespace DeathWish.Server;

internal enum WorldCommandType { Join, Leave }

internal readonly record struct WorldCommand(WorldCommandType Type, WorldAdmission Admission);

internal sealed class WorldAdmission(ClientSession session)
{
    private int _departed;

    internal ClientSession Session { get; } = session;
    internal bool IsDeparted => Volatile.Read(ref _departed) != 0;

    // This token identifies one admission, so an old departure cannot affect a later rejoin.
    internal void MarkDeparted() => Volatile.Write(ref _departed, 1);
}
