using DeathWish.Protocol;

namespace DeathWish.Server;

internal enum WorldCommandType { Join, Leave }

internal readonly record struct WorldCommand(WorldCommandType Type, WorldAdmission Admission);

internal sealed class WorldAdmission(ClientSession session, TimeProvider? timeProvider = null, SnapshotMode mode = SnapshotMode.Positions)
{
    private int _departed;

    internal ClientSession Session { get; } = session;
    internal MovementMailbox Movement { get; } = new(timeProvider ?? TimeProvider.System);
    internal SnapshotMode Mode { get; } = mode;
    internal bool IsDeparted => Volatile.Read(ref _departed) != 0;

    // This token identifies one admission, so an old departure cannot affect a later rejoin.
    internal void MarkDeparted()
    {
        Volatile.Write(ref _departed, 1);
        Movement.Close();
    }
}
