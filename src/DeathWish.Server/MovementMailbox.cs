using System.Numerics;
using DeathWish.Protocol;

namespace DeathWish.Server;

internal sealed class MovementMailbox
{
    internal const int INPUT_TIMEOUT_MS = 250;
    private const int MAX_PENDING_ACTIONS = 16;

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    // A null ray represents one jump press.
    private readonly Queue<CameraRay?> _actions = new();
    private MoveInput _latest;
    private long _receivedTimestamp;
    private bool _hasInput;
    private bool _isClosed;

    internal MovementMailbox(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>Thread-safe latest-input replacement. Rejected sequences do not refresh the receive timestamp.</summary>
    internal bool TrySetInput(MoveInput input)
    {
        input.Validate();
        lock (_sync)
        {
            if (_isClosed || (_hasInput && !MoveInput.IsNewerSequence(input.Sequence, _latest.Sequence)))
            {
                return false;
            }

            _receivedTimestamp = _timeProvider.GetTimestamp();
            _latest = input;
            _hasInput = true;
            return true;
        }
    }

    /// <summary>The world loop reads a copied direction; expired input stays sequenced but produces zero movement.</summary>
    internal Vector3 GetDirection()
    {
        lock (_sync)
        {
            if (_isClosed || !_hasInput)
            {
                return Vector3.Zero;
            }

            TimeSpan age = _timeProvider.GetElapsedTime(_receivedTimestamp, _timeProvider.GetTimestamp());
            if (age >= TimeSpan.FromMilliseconds(INPUT_TIMEOUT_MS))
            {
                return Vector3.Zero;
            }

            var direction = new Vector3(_latest.X, 0, _latest.Z);
            return direction.LengthSquared() > 1 ? Vector3.Normalize(direction) : direction;
        }
    }

    /// <summary>Queues an action from the network thread. A full queue rejects the input.</summary>
    internal void EnqueueAction(CameraRay? ray)
    {
        lock (_sync)
        {
            if (_isClosed)
            {
                return;
            }
            if (_actions.Count >= MAX_PENDING_ACTIONS)
            {
                throw new InvalidDataException("Too many pending player actions.");
            }
            _actions.Enqueue(ray);
        }
    }

    /// <summary>Removes the oldest action. Called by the world loop.</summary>
    internal bool TryTakeAction(out CameraRay? ray)
    {
        lock (_sync)
        {
            return _actions.TryDequeue(out ray);
        }
    }

    /// <summary>Clears input while bodyless and returns how many ray requests still need replies. Called by the world loop.</summary>
    internal int DiscardPendingInputs()
    {
        lock (_sync)
        {
            int discardedRays = _actions.Count(action => action.HasValue);
            _actions.Clear();
            _latest = new MoveInput(_latest.Sequence, 0f, 0f);
            return discardedRays;
        }
    }

    internal void Close()
    {
        lock (_sync)
        {
            _isClosed = true;
            _actions.Clear();
            _hasInput = false;
            _latest = default;
        }
    }
}
