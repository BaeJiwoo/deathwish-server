using System.Numerics;
using DeathWish.Server;
using DeathWish.Server.Physics;

static class WorldLoopTests
{
    public static async Task RunAsync()
    {
        VerifyWorlds();
        await VerifySchedule();
        await VerifyFractionalPeriod();
        await VerifyOverload();
        await VerifyCancellationAndFailure();
        await VerifyConcurrentStart();
        await VerifyWorldFailure();
        await VerifyHighRate();
        await VerifyRealClock();
        Console.WriteLine("PASS world ownership, fixed schedule, catch-up bounds, statistics, cancellation, and loop failure");
    }

    private static void VerifyWorlds()
    {
        var rooms = new RoomManager(new SessionManager(), 3);
        GameWorld[] worlds = rooms.GetWorlds();
        Require(worlds.Select(world => world.RoomId).SequenceEqual(new[] { 1, 2, 3 }), "Ordered room worlds");
        Require(worlds.All(world => world.CurrentTick == 0) && worlds.Distinct().Count() == 3, "Independent new worlds");
        VerifyWorldFloors(worlds);
        Require(rooms.TryGetRoom(2, out var room) && ReferenceEquals(room.World, worlds[1]), "Room owns its world");
        worlds[0] = worlds[1];
        Require(rooms.GetWorlds()[0].RoomId == 1, "World array is a copy");

        var world = new GameWorld(1);
        world.Tick(1f / 30);
        Require(world.CurrentTick == 1, "Completed tick increases once");
        foreach (float delta in new[] { 0, -1, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Reject<ArgumentOutOfRangeException>(() => world.Tick(delta));
        Require(world.CurrentTick == 1, "Rejected update does not count as a tick");
        Reject<ArgumentOutOfRangeException>(() => new GameWorld(0));
        Reject<ArgumentNullException>(() => new WorldLoop(null!, 30));
        Reject<ArgumentException>(() => new WorldLoop(Array.Empty<GameWorld>(), 30));
        Reject<ArgumentException>(() => new WorldLoop(new GameWorld[] { null! }, 30));
        Reject<ArgumentException>(() => new WorldLoop(new[] { world, new GameWorld(1) }, 30));
        Reject<ArgumentOutOfRangeException>(() => new WorldLoop(new[] { world }, 0));
    }

    private static void VerifyWorldFloors(GameWorld[] worlds)
    {
        foreach (GameWorld world in worlds[..^1])
        {
            if (world.Colliders.Count != 1 || world.Colliders[0] is not PlaneCollider floor)
            {
                throw new Exception("Each flat room must start with exactly one floor plane.");
            }

            Require(floor.CenterWorldPositionMeters == Vector3.Zero && floor.WidthMeters == 100 && floor.DepthMeters == 100,
                "Floor is centered at the origin and measures 100 m on both axes");
            Require(floor.GetBounds() == (new Vector3(-50, 0, -50), new Vector3(50, 0, 50)), "Floor world bounds");
        }

        Require(worlds.Select(world => world.Colliders).Distinct().Count() == worlds.Length, "Independent collider lists");
        Require(worlds.Select(world => world.Colliders[0]).Distinct().Count() == worlds.Length, "Independent floor instances");
    }

    private static async Task VerifySchedule()
    {
        var clock = new ManualTimeProvider();
        var first = new GameWorld(1);
        var second = new GameWorld(2);
        var supplied = new[] { second, first };
        var loop = new WorldLoop(supplied, 10, clock);
        supplied[0] = new GameWorld(99);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);
        try
        {
            Require(await clock.NextDelayAsync() == TimeSpan.FromMilliseconds(100), "First tick waits one period");
            Require(first.CurrentTick == 0 && loop.GetStatistics() == default, "No immediate first tick");
            await RejectAsync<InvalidOperationException>(() => loop.RunAsync(default));
            clock.Advance(TimeSpan.FromMilliseconds(99));
            Require(first.CurrentTick == 0, "No early tick");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Require(await clock.NextDelayAsync() == TimeSpan.FromMilliseconds(100), "Next absolute deadline");
            Require(first.CurrentTick == 1 && second.CurrentTick == 1 && supplied[0].CurrentTick == 0, "Each copied world advances once");

            clock.Advance(TimeSpan.FromMilliseconds(250));
            Require(await clock.NextDelayAsync() == TimeSpan.FromMilliseconds(50), "Late wake preserves schedule phase");
            Require(first.CurrentTick == 3 && second.CurrentTick == 3, "Two overdue ticks caught up");
            Require(loop.GetStatistics().CompletedTickCount == 3 && loop.GetStatistics().DroppedTime == TimeSpan.Zero,
                "Count server ticks rather than rooms");
            Require(loop.FixedDeltaTime == 0.1f, "Late wake does not change simulation delta");

            long measured = first.CurrentTick;
            clock.OnTimestamp = () =>
            {
                if (first.CurrentTick == measured) return;
                measured = first.CurrentTick;
                clock.Advance(TimeSpan.FromMilliseconds(20));
            };
            clock.Advance(TimeSpan.FromMilliseconds(50));
            Require(await clock.NextDelayAsync() == TimeSpan.FromMilliseconds(80), "Work time is deducted from next wait");
            WorldLoopStatistics stats = loop.GetStatistics();
            Require(stats.CompletedTickCount == 4 && stats.LastTickDuration == TimeSpan.FromMilliseconds(20)
                && stats.MaxTickDuration == stats.LastTickDuration && stats.OverrunCount == 0, "Under-budget duration statistics");
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        Require(first.CurrentTick == 4, "Stopped loop does not tick again");
        await RejectAsync<InvalidOperationException>(() => loop.RunAsync(default));
    }

    private static async Task VerifyOverload()
    {
        var clock = new ManualTimeProvider();
        var world = new GameWorld(1);
        var loop = new WorldLoop(new[] { world }, 10, clock);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);
        try
        {
            await clock.NextDelayAsync();
            clock.Advance(TimeSpan.FromMilliseconds(1050));
            Require(await clock.NextDelayAsync() == TimeSpan.FromMilliseconds(50), "Skip backlog to the next future deadline");
            WorldLoopStatistics stats = loop.GetStatistics();
            Require(world.CurrentTick == 4 && stats.CompletedTickCount == 4, "At most four ticks per catch-up batch");
            Require(stats.DroppedTime == TimeSpan.FromMilliseconds(600) && stats.OverrunCount == 0, "Scheduler lateness is not a processing overrun");

            long measured = world.CurrentTick;
            clock.OnTimestamp = () =>
            {
                if (world.CurrentTick == measured) return;
                measured = world.CurrentTick;
                clock.Advance(TimeSpan.FromMilliseconds(150));
            };
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await clock.NextDelayAsync();
            stats = loop.GetStatistics();
            Require(world.CurrentTick == 8 && stats.OverrunCount == 4, "Sustained overload remains bounded");
            Require(stats.LastTickDuration == TimeSpan.FromMilliseconds(150) && stats.MaxTickDuration == stats.LastTickDuration,
                "Whole tick duration excludes waits");
            Require(stats.DroppedTime == TimeSpan.FromMilliseconds(900), "Dropped intervals accumulate");
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task VerifyFractionalPeriod()
    {
        var clock = new ManualTimeProvider();
        var world = new GameWorld(1);
        var loop = new WorldLoop(new[] { world }, 30, clock);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);
        try
        {
            TimeSpan elapsed = TimeSpan.Zero;
            for (int count = 0; count < 300; count++)
            {
                TimeSpan delay = await clock.NextDelayAsync();
                elapsed += delay;
                clock.Advance(delay);
            }
            await clock.NextDelayAsync();
            Require(world.CurrentTick == 300 && elapsed == TimeSpan.FromSeconds(10), "30 Hz does not accumulate rounded-period drift");
            Require(loop.FixedDeltaTime == 1f / 30 && loop.GetStatistics().DroppedTime == TimeSpan.Zero,
                "Fractional-period schedule preserves fixed simulation delta");
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task VerifyCancellationAndFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var world = new GameWorld(1);
        await new WorldLoop(new[] { world }, 30).RunAsync(cancellation.Token);
        Require(world.CurrentTick == 0, "Pre-cancellation performs no work");
        world = new GameWorld(1);

        var clock = new ManualTimeProvider();
        var other = new GameWorld(2);
        using var duringTick = new CancellationTokenSource();
        var loop = new WorldLoop(new[] { world, other }, 10, clock);
        clock.OnTimestamp = () => { if (world.CurrentTick == 1) duringTick.Cancel(); };
        Task running = loop.RunAsync(duringTick.Token);
        await clock.NextDelayAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Require(world.CurrentTick == 1 && other.CurrentTick == 1 && loop.GetStatistics().CompletedTickCount == 1,
            "Cancellation finishes a whole server tick and prevents further catch-up");

        var failingClock = new ManualTimeProvider();
        var failure = new InvalidOperationException("Injected clock failure");
        var failing = new WorldLoop(new[] { new GameWorld(1) }, 10, failingClock);
        Task failed = failing.RunAsync(default);
        await failingClock.NextDelayAsync();
        failingClock.OnTimestamp = () => throw failure;
        failingClock.Advance(TimeSpan.FromMilliseconds(100));
        try { await failed.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Expected loop failure"); }
        catch (InvalidOperationException exception) when (ReferenceEquals(exception, failure)) { }
    }

    private static async Task VerifyHighRate()
    {
        var clock = new ManualTimeProvider();
        var world = new GameWorld(1);
        var loop = new WorldLoop(new[] { world }, int.MaxValue, clock);
        int readsAfterFourthTick = 0;
        clock.OnTimestamp = () =>
        {
            if (world.CurrentTick == 4 && ++readsAfterFourthTick == 2)
                clock.Advance(TimeSpan.FromMilliseconds(1));
        };
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);
        try
        {
            Require(await clock.NextDelayAsync() == TimeSpan.FromMilliseconds(1), "Sub-millisecond periods yield to timer");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Require(await clock.NextDelayAsync() == TimeSpan.FromMilliseconds(1), "Extreme rates still yield after bounded work");
            Require(world.CurrentTick == 4 && loop.GetStatistics().DroppedTime > TimeSpan.Zero, "Extreme rates drop excess ticks");
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task VerifyWorldFailure()
    {
        var first = new GameWorld(1);
        var failing = new GameWorld(2);
        var last = new GameWorld(3);
        // Force the terminal counter boundary without adding a production mutation API for tests.
        typeof(GameWorld).GetField("_currentTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(failing, long.MaxValue);
        var clock = new ManualTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var loop = new WorldLoop(new[] { last, failing, first }, 10, clock);
        Task running = loop.RunAsync(cancellation.Token);
        try
        {
            await clock.NextDelayAsync();
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await RejectAsync<OverflowException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            Require(first.CurrentTick == 1 && failing.CurrentTick == long.MaxValue && last.CurrentTick == 0,
                "Worlds execute in room order and a failed update stops remaining rooms");
            Require(loop.GetStatistics().CompletedTickCount == 0, "Partial server tick is not counted as complete");
        }
        finally { cancellation.Cancel(); }
    }

    private static async Task VerifyConcurrentStart()
    {
        var clock = new ManualTimeProvider();
        var loop = new WorldLoop(new[] { new GameWorld(1) }, 30, clock);
        using var cancellation = new CancellationTokenSource();
        Task<bool>[] contenders = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try { await loop.RunAsync(cancellation.Token); return true; }
            catch (InvalidOperationException) { return false; }
        })).ToArray();
        try { await clock.NextDelayAsync(); }
        finally { cancellation.Cancel(); }
        bool[] results = await Task.WhenAll(contenders).WaitAsync(TimeSpan.FromSeconds(5));
        Require(results.Count(started => started) == 1, "Only one concurrent caller can own the loop");
    }

    private static async Task VerifyRealClock()
    {
        var worlds = new[] { new GameWorld(1), new GameWorld(2) };
        var loop = new WorldLoop(worlds, 30);
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = Task.Run(() => loop.RunAsync(cancellation.Token));
        try
        {
            while (worlds[0].CurrentTick == 0) await Task.Delay(10, deadline.Token);
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Require(worlds[0].CurrentTick == worlds[1].CurrentTick && worlds[0].CurrentTick == loop.GetStatistics().CompletedTickCount,
            "Real-clock cancellation leaves all worlds at a completed server tick");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
