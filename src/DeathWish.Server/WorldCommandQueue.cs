using System.Diagnostics.CodeAnalysis;

namespace DeathWish.Server;

internal sealed class WorldCommandQueue
{
    private readonly object _sync = new();
    private readonly Queue<WorldCommand> _commands = new();
    private readonly HashSet<WorldAdmission> _admissions = new();
    private readonly int _capacity;
    private readonly TimeProvider _timeProvider;
    private readonly bool _isArena;
    private bool _accepting = true;

    internal int PendingCount { get { lock (_sync) return _commands.Count; } }
    internal int AdmissionCount { get { lock (_sync) return _admissions.Count; } }
    internal bool IsAccepting { get { lock (_sync) return _accepting; } }

    internal WorldCommandQueue(int capacity, TimeProvider? timeProvider = null, bool isArena = false)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isArena = isArena;
    }

    internal bool TryEnqueueJoin(ClientSession session, [NotNullWhen(true)] out WorldAdmission? admission,
        string? nickname = null, bool receiveArenaShots = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            admission = null;
            if (!_accepting || session.IsDisconnected || _admissions.Count >= _capacity) return false;

            admission = new WorldAdmission(session, _timeProvider, nickname, _isArena, receiveArenaShots);
            _admissions.Add(admission);
            _commands.Enqueue(new WorldCommand(WorldCommandType.Join, admission));
            return true;
        }
    }

    internal void EnqueueLeave(WorldAdmission admission)
    {
        lock (_sync)
        {
            if (!_admissions.Contains(admission) || admission.IsDeparted) return;

            // Every reservation permits one Join and one Leave; pending commands never exceed twice capacity.
            admission.MarkDeparted();
            _commands.Enqueue(new WorldCommand(WorldCommandType.Leave, admission));
        }
    }

    internal bool TryDequeue(out WorldCommand command)
    {
        lock (_sync) return _commands.TryDequeue(out command);
    }

    internal void Release(WorldAdmission admission)
    {
        lock (_sync) _admissions.Remove(admission);
    }

    internal void Complete()
    {
        lock (_sync)
        {
            _accepting = false;
            foreach (WorldAdmission admission in _admissions) admission.MarkDeparted();
            _commands.Clear();
            _admissions.Clear();
        }
    }
}
