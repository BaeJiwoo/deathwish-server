using DeathWish.Server;
using DeathWish.Protocol;
using DotNetty.Transport.Channels.Embedded;

static class RoomTests
{
    public static async Task RunAsync()
    {
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions);
        for (int iteration = 0; iteration < 32; iteration++)
        {
            var handler = new GameChannelHandler(sessions, rooms);
            var channel = new EmbeddedChannel(handler);
            try
            {
                Guid id = handler.Session!.SessionId;
                using var start = new ManualResetEventSlim();
                Task<RoomJoinResult> firstJoin = Task.Run(() => { start.Wait(); return rooms.Join(id, 1); });
                Task<RoomJoinResult> secondJoin = Task.Run(() => { start.Wait(); return rooms.Join(id, 2); });
                start.Set();
                RoomJoinResult[] results = await Task.WhenAll(firstJoin, secondJoin);
                if (results.Count(result => result == RoomJoinResult.Success) != 1 ||
                    results.Count(result => result == RoomJoinResult.AlreadyInAnotherRoom) != 1)
                    throw new Exception("Concurrent admissions must select exactly one room");

                rooms.Leave(id);
                using var raceStart = new ManualResetEventSlim();
                Task join = Task.Run(() =>
                {
                    raceStart.Wait();
                    for (int attempt = 0; attempt < 100; attempt++) rooms.Join(id, 1);
                });
                raceStart.Set();
                await channel.CloseAsync();
                channel.RunPendingTasks();
                await join;

                if (rooms.GetRoomId(id) is not null || rooms.Join(id, 2) != RoomJoinResult.SessionUnavailable)
                    throw new Exception("Admission racing disconnection left a member behind");
            }
            finally { channel.FinishAndReleaseAll(); }
        }

        if (sessions.Count != 0 || !rooms.TryGetRoom(1, out var first) || first.MemberCount != 0 ||
            !rooms.TryGetRoom(2, out var second) || second.MemberCount != 0)
            throw new Exception("Concurrent room lifecycle leaked membership");
        Console.WriteLine("PASS Concurrent room admissions and disconnect races");
    }
}
