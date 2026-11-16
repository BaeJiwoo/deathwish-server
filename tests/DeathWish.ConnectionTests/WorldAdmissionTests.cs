using System.Numerics;
using DeathWish.Protocol;
using DeathWish.Server;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;

static class WorldAdmissionTests
{
    public static async Task RunAsync()
    {
        VerifyAdmissionAndRejoin();
        VerifyCapacityAndDeparture();
        VerifyActiveCapacityAndOldDeparture();
        VerifyImmediateDisconnect();
        await VerifyConcurrentDeparture();
        await VerifyLoopCleanup(false);
        await VerifyLoopCleanup(true);
        await VerifyPreCancelledCleanup();
        Console.WriteLine("PASS world admission, player identities, bounded FIFO commands, guaranteed departure, races, and terminal cleanup");
    }

    private static void VerifyAdmissionAndRejoin()
    {
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions, 2, 4, 1);
        var handler = new GameChannelHandler(sessions, rooms);
        var channel = new EmbeddedChannel(handler);
        GameWorld world = rooms.GetWorlds()[0];
        Guid sessionId = handler.Session!.SessionId;
        try
        {
            Require(rooms.Join(sessionId, 1) == RoomJoinResult.Success, "Admission reserves a slot");
            Require(rooms.Join(sessionId, 1) == RoomJoinResult.Success && world.Commands.PendingCount == 1,
                "Duplicate admission does not queue another Join");
            Require(world.PlayerCount == 0 && world.Commands.AdmissionCount == 1, "Player creation waits for a tick");
            world.Tick(1f / 30);
            PlayerState first = world.GetPlayers().Single();
            Require(first.PlayerId == 1 && first.SessionId == sessionId && first.Position == Vector3.Zero, "World assigns player identity and initial position");
            PlayerState[] copy = world.GetPlayers();
            copy[0] = copy[0] with { Position = Vector3.One };
            Require(world.GetPlayers()[0].Position == Vector3.Zero, "Copied snapshots cannot mutate world state");
            Require(rooms.Join(sessionId, 2) == RoomJoinResult.AlreadyInAnotherRoom && rooms.GetWorlds()[1].Commands.PendingCount == 0,
                "Rejected room switch does not enqueue a world command");

            Require(rooms.Leave(sessionId) && !rooms.Leave(sessionId), "Repeated Leave is idempotent");
            Require(rooms.Join(sessionId, 1) == RoomJoinResult.Success, "Connected session can reserve a new admission");
            Require(world.PlayerCount == 1 && world.Commands.PendingCount == 2, "Network callbacks do not mutate player state");
            world.Tick(1f / 30);
            Require(world.PlayerCount == 0 && world.Commands.PendingCount == 1, "FIFO removes old admission before processing rejoin");
            world.Tick(1f / 30);
            Require(world.GetPlayers().Single().PlayerId == 2, "Rejoin gets a new player ID");
        }
        finally { channel.FinishAndReleaseAll(); }
        world.Tick(1f / 30);
        Require(world.PlayerCount == 0 && world.Commands.AdmissionCount == 0 && sessions.Count == 0, "Disconnect releases the final reservation");
    }

    private static void VerifyCapacityAndDeparture()
    {
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions, 1, 2, 1);
        var handlers = Enumerable.Range(0, 3).Select(_ => new GameChannelHandler(sessions, rooms)).ToArray();
        var channels = handlers.Select(handler => new EmbeddedChannel(handler)).ToArray();
        GameWorld world = rooms.GetWorlds()[0];
        try
        {
            Require(rooms.Join(handlers[0].Session!.SessionId, 1) == RoomJoinResult.Success, "First bounded admission");
            Require(rooms.Join(handlers[1].Session!.SessionId, 1) == RoomJoinResult.Success, "Second bounded admission");
            channels[2].WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(1))));
            IByteBuffer response = channels[2].ReadOutbound<IByteBuffer>();
            try
            {
                byte[] frame = new byte[response.ReadableBytes];
                response.ReadBytes(frame);
                Require(frame.SequenceEqual(new byte[] { 0, 5, 4, 0, 0, 0, 1, 4 }), "Capacity rejection uses WorldUnavailable wire value");
            }
            finally { response.Release(); }
            Require(channels[2].Active && rooms.GetRoomId(handlers[2].Session!.SessionId) is null, "Rejected admission keeps connection without membership");
            Require(world.Commands.PendingCount == 2 && world.Commands.AdmissionCount == 2, "Rejected admission does not consume queue capacity");

            channels[0].FinishAndReleaseAll();
            channels[1].FinishAndReleaseAll();
            Require(world.Commands.PendingCount == 4, "Full admission capacity still permits both departures");
            for (int count = 0; count < 4; count++)
            {
                world.Tick(1f / 30);
                Require(world.Commands.PendingCount == 3 - count && world.PlayerCount == 0, "Every command including stale Join consumes the tick budget");
            }
            Require(world.Commands.AdmissionCount == 0, "Departure processing frees admission capacity");
            Require(rooms.Join(handlers[2].Session!.SessionId, 1) == RoomJoinResult.Success, "Rejected client can retry after capacity is freed");
            world.Tick(1f / 30);
            Require(world.PlayerCount == 1, "Retry creates a player");
            rooms.StopAdmission();
            Require(rooms.Join(handlers[2].Session!.SessionId, 1) == RoomJoinResult.WorldUnavailable, "Shutdown refuses further requests");
        }
        finally
        {
            foreach (var channel in channels) channel.FinishAndReleaseAll();
        }
        world.Tick(1f / 30);
        Require(world.PlayerCount == 0 && world.Commands.AdmissionCount == 0, "Shutdown admission gate does not block departures");
    }

    private static void VerifyImmediateDisconnect()
    {
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions, 1);
        var handler = new GameChannelHandler(sessions, rooms);
        var channel = new EmbeddedChannel(handler);
        GameWorld world = rooms.GetWorlds()[0];
        Require(rooms.Join(handler.Session!.SessionId, 1) == RoomJoinResult.Success, "Join before immediate disconnect");
        channel.FinishAndReleaseAll();
        world.Tick(1f / 30);
        Require(world.PlayerCount == 0 && world.Commands.PendingCount == 0 && world.Commands.AdmissionCount == 0,
            "Join followed by immediate disconnect cannot leave a player");

        var otherHandler = new GameChannelHandler(sessions, rooms);
        var otherChannel = new EmbeddedChannel(otherHandler);
        try
        {
            rooms.Join(otherHandler.Session!.SessionId, 1);
            world.Tick(1f / 30);
            Require(world.GetPlayers().Single().PlayerId == 1, "Skipped stale Join does not allocate a player ID");
        }
        finally { otherChannel.FinishAndReleaseAll(); }
        world.Tick(1f / 30);
    }

    private static async Task VerifyConcurrentDeparture()
    {
        for (int iteration = 0; iteration < 32; iteration++)
        {
            var sessions = new SessionManager();
            var rooms = new RoomManager(sessions, 1, 4, 1);
            var handler = new GameChannelHandler(sessions, rooms);
            var channel = new EmbeddedChannel(handler);
            GameWorld world = rooms.GetWorlds()[0];
            using var start = new ManualResetEventSlim();
            Task join = Task.Run(() => { start.Wait(); rooms.Join(handler.Session!.SessionId, 1); });
            Task tick = Task.Run(() => { start.Wait(); world.Tick(1f / 30); });
            start.Set();
            await channel.CloseAsync();
            channel.RunPendingTasks();
            await Task.WhenAll(join, tick);
            channel.FinishAndReleaseAll();
            while (world.Commands.PendingCount > 0) world.Tick(1f / 30);
            Require(world.PlayerCount == 0 && world.Commands.AdmissionCount == 0 && sessions.Count == 0,
                "Concurrent Join, tick, and disconnect leave no players or reservations");
        }
    }

    private static async Task VerifyLoopCleanup(bool fail)
    {
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions, 2);
        var handlers = Enumerable.Range(0, 2).Select(_ => new GameChannelHandler(sessions, rooms)).ToArray();
        var channels = handlers.Select(handler => new EmbeddedChannel(handler)).ToArray();
        var clock = new ManualTimeProvider();
        using var cancellation = new CancellationTokenSource();
        GameWorld[] worlds = rooms.GetWorlds();
        var loop = new WorldLoop(worlds, 10, clock);
        Task running = loop.RunAsync(cancellation.Token);
        try
        {
            await clock.NextDelayAsync();
            for (int index = 0; index < handlers.Length; index++) rooms.Join(handlers[index].Session!.SessionId, index + 1);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await clock.NextDelayAsync();
            Require(worlds.All(world => world.PlayerCount == 1), "Both worlds contain an active player before shutdown");
            rooms.Leave(handlers[0].Session!.SessionId);
            rooms.Join(handlers[0].Session!.SessionId, 1);
            if (fail)
            {
                var failure = new InvalidOperationException("Injected failure with live players");
                clock.OnTimestamp = () => throw failure;
                clock.Advance(TimeSpan.FromMilliseconds(100));
                try { await running.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Expected loop failure"); }
                catch (InvalidOperationException exception) when (ReferenceEquals(exception, failure)) { }
            }
            else
            {
                cancellation.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Require(worlds.All(world => world.PlayerCount == 0 && world.Commands.AdmissionCount == 0 && world.Commands.PendingCount == 0),
                "Loop finalization clears active players and pending commands in every world");
            Require(!worlds[0].Commands.TryEnqueueJoin(handlers[0].Session!, out _), "Stopped world rejects future admission");
            Require(rooms.Join(handlers[0].Session!.SessionId, 1) == RoomJoinResult.WorldUnavailable,
                "Stopped world rejects duplicate room requests too");
        }
        finally
        {
            cancellation.Cancel();
            foreach (var channel in channels) channel.FinishAndReleaseAll();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch when (running.IsCompleted) { }
        }
    }

    private static async Task VerifyPreCancelledCleanup()
    {
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions, 1);
        var handler = new GameChannelHandler(sessions, rooms);
        var channel = new EmbeddedChannel(handler);
        GameWorld world = rooms.GetWorlds()[0];
        try
        {
            rooms.Join(handler.Session!.SessionId, 1);
            await new WorldLoop(new[] { world }, 30).RunAsync(new CancellationToken(true));
            Require(world.PlayerCount == 0 && world.Commands.AdmissionCount == 0 && world.Commands.PendingCount == 0,
                "Pre-cancelled loop clears queued admission without ticking");
        }
        finally { channel.FinishAndReleaseAll(); }
    }

    private static void VerifyActiveCapacityAndOldDeparture()
    {
        var channel = new EmbeddedChannel();
        var session = new ClientSession(channel);
        var other = new ClientSession(channel);
        var world = new GameWorld(1, 1, 1);
        try
        {
            Require(world.Commands.TryEnqueueJoin(session, out var first), "Reserve the only slot");
            world.Tick(1f / 30);
            Require(world.Commands.PendingCount == 0 && !world.Commands.TryEnqueueJoin(other, out _),
                "Active players retain admission capacity after Join is dequeued");
            world.Commands.EnqueueLeave(first!);
            world.Commands.EnqueueLeave(first!);
            Require(world.Commands.PendingCount == 1 && !world.Commands.TryEnqueueJoin(other, out _),
                "Duplicate Leave is coalesced and capacity stays reserved until removal");
            world.Tick(1f / 30);
            Require(world.Commands.TryEnqueueJoin(session, out var second), "Reuse capacity with a new admission token");
            world.Tick(1f / 30);
            world.Commands.EnqueueLeave(first!);
            world.Tick(1f / 30);
            Require(world.Commands.PendingCount == 0 && world.GetPlayers().Single().PlayerId == 2,
                "Delayed old Leave cannot remove the new admission");
            world.Commands.EnqueueLeave(second!);
            world.Tick(1f / 30);
            Require(world.PlayerCount == 0 && world.Commands.AdmissionCount == 0, "New admission departs normally");
        }
        finally { channel.FinishAndReleaseAll(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
