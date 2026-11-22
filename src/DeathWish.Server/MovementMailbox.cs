using System.Numerics;
using DeathWish.Protocol;

namespace DeathWish.Server;

internal sealed class MovementMailbox
{
    internal const int INPUT_TIMEOUT_MS = 250;

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private const int MAX_PENDING_ACTIONS = 16;
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

    internal bool TryTakeAction(out CameraRay? ray)
    {
        lock (_sync)
        {
            return _actions.TryDequeue(out ray);
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
