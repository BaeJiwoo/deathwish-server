namespace DeathWish.Server;

internal sealed class WorldLoop
{
    internal const int MaxCatchUpTicks = 4;

    private readonly GameWorld[] _worlds;
    private readonly TimeProvider _timeProvider;
    private readonly object _statisticsSync = new();
    private WorldLoopStatistics _statistics;
    private int _started;

    internal int TickRate { get; }
    internal float FixedDeltaTime { get; }

    internal WorldLoop(IReadOnlyList<GameWorld> worlds, int tickRate, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(worlds);
        if (tickRate < 1) throw new ArgumentOutOfRangeException(nameof(tickRate));
        _worlds = worlds.ToArray();
        if (_worlds.Length == 0 || _worlds.Any(world => world is null))
            throw new ArgumentException("Expected at least one non-null world.", nameof(worlds));
        if (_worlds.Select(world => world.RoomId).Distinct().Count() != _worlds.Length)
            throw new ArgumentException("World room IDs must be unique.", nameof(worlds));

        Array.Sort(_worlds, (first, second) => first.RoomId.CompareTo(second.RoomId));
        TickRate = tickRate;
        FixedDeltaTime = 1f / tickRate;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal WorldLoopStatistics GetStatistics()
    {
        lock (_statisticsSync) return _statistics;
    }

    /// <summary>Runs once until cancellation. Updates are serial; simulation exceptions propagate to the owner.</summary>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A world loop can only run once.");
        if (cancellationToken.IsCancellationRequested) return;

        long origin = _timeProvider.GetTimestamp();
        long frequency = _timeProvider.TimestampFrequency;
        long nextTick = 1;
        long droppedTicks = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            // Compare rational deadlines without rounding the period or accumulating floating-point drift.
            Int128 elapsed = ElapsedPeriods(_timeProvider.GetTimestamp());
            Int128 remaining = (Int128)nextTick * frequency - elapsed;
            if (remaining > 0)
            {
                await WaitAsync(remaining).ConfigureAwait(false);
                continue;
            }

            for (int count = 0; count < MaxCatchUpTicks && elapsed >= (Int128)nextTick * frequency; count++)
            {
                if (cancellationToken.IsCancellationRequested) return;

                long started = _timeProvider.GetTimestamp();
                // Once started, finish every room before honoring cancellation.
                foreach (GameWorld world in _worlds) world.Tick(FixedDeltaTime);
                long finished = _timeProvider.GetTimestamp();
                TimeSpan duration = _timeProvider.GetElapsedTime(started, finished);
                lock (_statisticsSync)
                {
                    _statistics = _statistics with
                    {
                        CompletedTickCount = _statistics.CompletedTickCount + 1,
                        OverrunCount = _statistics.OverrunCount + ((Int128)(finished - started) * TickRate > frequency ? 1 : 0),
                        LastTickDuration = duration,
                        MaxTickDuration = duration > _statistics.MaxTickDuration ? duration : _statistics.MaxTickDuration
                    };
                }

                nextTick = checked(nextTick + 1);
                elapsed = ElapsedPeriods(finished);
            }

            if (cancellationToken.IsCancellationRequested) return;
            if (elapsed >= (Int128)nextTick * frequency)
            {
                // Advance the schedule, not simulation state, when the catch-up budget is exhausted.
                long skipped = checked((long)(elapsed / frequency) - nextTick + 1);
                nextTick = checked(nextTick + skipped);
                droppedTicks = checked(droppedTicks + skipped);
                var droppedTime = TimeSpan.FromTicks(checked((long)((Int128)droppedTicks * TimeSpan.TicksPerSecond / TickRate)));
                lock (_statisticsSync)
                    _statistics = _statistics with { DroppedTime = droppedTime };

                // Always yield after dropping backlog, even if bookkeeping has already passed the next deadline.
                await WaitAsync((Int128)nextTick * frequency - ElapsedPeriods(_timeProvider.GetTimestamp())).ConfigureAwait(false);
            }
        }

        Int128 ElapsedPeriods(long timestamp) => (Int128)(timestamp - origin) * TickRate;

        async Task WaitAsync(Int128 remaining)
        {
            // Round up to a millisecond; periods below timer resolution must not become a busy loop.
            Int128 denominator = (Int128)frequency * TickRate;
            long milliseconds = remaining > 0 ? checked((long)((remaining * 1000 + denominator - 1) / denominator)) : 1;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
}
