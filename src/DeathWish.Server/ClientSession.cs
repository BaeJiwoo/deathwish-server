using DotNetty.Transport.Channels;
using DotNetty.Buffers;
using DeathWish.Protocol;

namespace DeathWish.Server;

public sealed class ClientSession
{
    internal const int MAX_PENDING_FRAMES = 64;
    internal const int MAX_PENDING_BYTES = 256 * 1024;

    private byte[]? _pendingSnapshot;
    private int _disconnected;
    private readonly object _outputSync = new();
    private readonly Queue<byte[]> _output = new();
    private int _pendingBytes;
    private int _pendingFrames;
    private bool _isDraining;
    private bool _isOutputStopped;
    internal TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(5);
    internal int PendingFrames
    {
        get
        {
            lock (_outputSync)
            {
                return _pendingFrames;
            }
        }
    }

    public Guid SessionId { get; } = Guid.NewGuid();
    public IChannel Channel { get; }
    public bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;

    internal ClientSession(IChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        Channel = channel;
    }

    internal void ReplyToJoin(RoomManager rooms, int roomId)
    {
        // Hold the output gate across admission so the world cannot enqueue JoinAck before this response.
        lock (_outputSync)
        {
            if (_isOutputStopped || IsDisconnected)
            {
                return;
            }
            RoomJoinResult result = rooms.Join(SessionId, roomId);
            Send(PacketCodec.EncodeJoinRoomResponse(new JoinRoomResponse(roomId, result)));
        }
    }

    /// <summary>Transfers ownership of an immutable frame to the bounded FIFO. Never waits for socket completion.</summary>
    internal bool Send(byte[] frame)
    {
        lock (_outputSync)
        {
            if (_isOutputStopped || IsDisconnected)
            {
                return false;
            }
            if (_pendingFrames >= MAX_PENDING_FRAMES || frame.Length > MAX_PENDING_BYTES - _pendingBytes)
            {
                StopOutput();
                _ = CloseAsync();
                return false;
            }

            _output.Enqueue(frame);
            _pendingFrames++;
            _pendingBytes += frame.Length;
            if (!_isDraining)
            {
                _isDraining = true;
                _ = DrainAsync();
            }
            return true;
        }
    }

    /// <summary>Shares an immutable frame with the transport. Replaces only a snapshot whose write has not started. Never awaits socket I/O.</summary>
    internal bool SendSnapshot(byte[] frame)
    {
        lock (_outputSync)
        {
            if (_isOutputStopped || IsDisconnected)
            {
                return false;
            }
            if (_pendingSnapshot != null)
            {
                _pendingBytes -= _pendingSnapshot.Length;
                _pendingFrames--;
            }
            _pendingSnapshot = null;
            if (_pendingFrames >= MAX_PENDING_FRAMES || frame.Length > MAX_PENDING_BYTES - _pendingBytes)
            {
                StopOutput();
                _ = CloseAsync();
                return false;
            }
            _pendingSnapshot = frame;
            _pendingBytes += frame.Length;
            _pendingFrames++;
            if (!_isDraining)
            {
                _isDraining = true;
                _ = DrainAsync();
            }
            return true;
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            byte[] frame;
            lock (_outputSync)
            {
                if (!_output.TryDequeue(out frame!))
                {
                    if (_pendingSnapshot == null)
                    {
                        _isDraining = false;
                        return;
                    }
                    frame = _pendingSnapshot;
                    _pendingSnapshot = null;
                }
            }

            bool hasFailed = false;
            try
            {
                // The pipeline owns the buffer after WriteAndFlushAsync, including failure and timeout.
                await Channel.WriteAndFlushAsync(Unpooled.WrappedBuffer(frame)).WaitAsync(WriteTimeout).ConfigureAwait(false);
            }
            catch (Exception)
            {
                hasFailed = true;
                StopOutput();
                await CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (_outputSync)
                {
                    _pendingFrames--;
                    _pendingBytes -= frame.Length;
                }
            }
            if (hasFailed)
            {
                return;
            }
        }
    }

    private async Task CloseAsync()
    {
        try
        {
            await Channel.CloseAsync().ConfigureAwait(false);
        }
        // The transport may already be shutting down; cleanup must not fault an unobserved task.
        catch (Exception)
        {
        }
    }

    private void StopOutput()
    {
        lock (_outputSync)
        {
            _isOutputStopped = true;
            if (_pendingSnapshot != null)
            {
                _pendingBytes -= _pendingSnapshot.Length;
                _pendingFrames--;
                _pendingSnapshot = null;
            }
            while (_output.TryDequeue(out var frame))
            {
                _pendingFrames--;
                _pendingBytes -= frame.Length;
            }
        }
    }

    // ChannelInactive owns the terminal transition; repeated notifications must not repeat cleanup.
    internal bool TryMarkDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
        {
            return false;
        }
        StopOutput();
        return true;
    }
}
