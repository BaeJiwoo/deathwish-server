using System.Threading.Channels;

sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = new();
    private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();
    private long _timestamp;

    public Action? OnTimestamp { get; set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        OnTimestamp?.Invoke();
        lock (_sync) return _timestamp;
    }

    public async Task<TimeSpan> NextDelayAsync()
        => await _delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        ManualTimer[] due;
        lock (_sync)
        {
            _timestamp += elapsed.Ticks;
            due = _timers.Where(timer => timer.Deadline <= _timestamp).ToArray();
            foreach (ManualTimer timer in due) _timers.Remove(timer);
        }
        foreach (ManualTimer timer in due) timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        public long Deadline { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot waits are used in these tests.");
            lock (owner._sync)
            {
                if (_disposed) return false;
                owner._timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Deadline = owner._timestamp + dueTime.Ticks;
                    owner._timers.Add(this);
                    owner._delays.Writer.TryWrite(dueTime);
                }
                return true;
            }
        }

        public void Fire()
        {
            lock (owner._sync)
            {
                if (_disposed) return;
            }
            callback(state);
        }

        public void Dispose()
        {
            lock (owner._sync)
            {
                _disposed = true;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
