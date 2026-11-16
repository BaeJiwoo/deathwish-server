using DeathWish.Server;
using DeathWish.Protocol;
using DotNetty.Transport.Channels.Embedded;

static class RoomTests
{
    public static async Task RunAsync()
    {
        foreach (int count in new[] { 1, 5 })
        {
            var options = new ServerOptions(roomCount: count);
            var configuredSessions = new SessionManager();
            var configuredRooms = new RoomManager(configuredSessions, options.RoomCount);
            for (int id = 1; id <= count; id++)
            {
                if (!configuredRooms.TryGetRoom(id, out var room) || room.RoomId != id || room.MemberCount != 0)
                    throw new Exception("Configured room was not preallocated");
            }
            if (configuredRooms.TryGetRoom(0, out _) || configuredRooms.TryGetRoom(count + 1, out _))
                throw new Exception("Room exists outside configured range");
            var configuredHandler = new GameChannelHandler(configuredSessions, configuredRooms);
            var configuredChannel = new EmbeddedChannel(configuredHandler);
            try
            {
                if (configuredRooms.Join(configuredHandler.Session!.SessionId, count) != RoomJoinResult.Success)
                    throw new Exception("Cannot join last configured room");
            }
            finally { configuredChannel.FinishAndReleaseAll(); }
        }
        foreach (int invalidCount in new[] { 0, -1 })
        {
            try { _ = new ServerOptions(roomCount: invalidCount); throw new Exception("Invalid options accepted"); }
            catch (ArgumentOutOfRangeException) { }
            try { _ = new RoomManager(new SessionManager(), invalidCount); throw new Exception("Invalid room count accepted"); }
            catch (ArgumentOutOfRangeException) { }
        }
        Console.WriteLine("PASS Configurable room preallocation and count validation");

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
