using System.Buffers.Binary;
using System.Security.Cryptography;
using DeathWish.Protocol;

namespace DeathWish.Server;

/// <summary>Measures application round trips for one admission. The world probes and the network thread consumes replies.</summary>
internal sealed class MatchLatency(TimeProvider timeProvider)
{
    private const int PROBE_INTERVAL_MS = 2_000;
    private const int PROBE_TIMEOUT_MS = 5_000;

    private readonly object _sync = new();
    private bool _hasSentProbe;
    private long _lastProbeTimestamp;
    private long _outstandingToken;
    private int _roundTripMs = MatchState.UNKNOWN_PING_MS;

    internal int RoundTripMs => Volatile.Read(ref _roundTripMs);

    /// <summary>Reserves the next probe if due. Called by the world loop before enqueuing the frame.</summary>
    internal long? PollProbe()
    {
        lock (_sync)
        {
            long now = timeProvider.GetTimestamp();
            if (_hasSentProbe)
            {
                double elapsedMs = timeProvider.GetElapsedTime(_lastProbeTimestamp, now).TotalMilliseconds;
                bool isWaitingForReply = _outstandingToken != 0 && elapsedMs < PROBE_TIMEOUT_MS;
                if (isWaitingForReply || elapsedMs < PROBE_INTERVAL_MS)
                {
                    return null;
                }
                if (_outstandingToken != 0)
                {
                    Volatile.Write(ref _roundTripMs, MatchState.UNKNOWN_PING_MS);
                }
            }

            // Unpredictable tokens prevent a client from answering a probe before receiving it.
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            RandomNumberGenerator.Fill(bytes);
            long token = BinaryPrimitives.ReadInt64LittleEndian(bytes) & long.MaxValue;
            _outstandingToken = Math.Max(token, 1);
            _lastProbeTimestamp = now;
            _hasSentProbe = true;
            return _outstandingToken;
        }
    }

    /// <summary>Accepts only the current unexpired token. Called by the network thread.</summary>
    internal void RecordReply(long token)
    {
        lock (_sync)
        {
            if (_outstandingToken == 0 || token != _outstandingToken)
            {
                return;
            }

            double elapsedMs = timeProvider.GetElapsedTime(_lastProbeTimestamp, timeProvider.GetTimestamp()).TotalMilliseconds;
            _outstandingToken = 0;
            int roundTripMs = elapsedMs < PROBE_TIMEOUT_MS ? (int)Math.Round(elapsedMs) : MatchState.UNKNOWN_PING_MS;
            Volatile.Write(ref _roundTripMs, roundTripMs);
        }
    }
}
