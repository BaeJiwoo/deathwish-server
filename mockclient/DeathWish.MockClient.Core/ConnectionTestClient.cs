using System;
using System.IO;
using System.Net.Sockets;
using DeathWish.Protocol;
using System.Threading;
using System.Threading.Tasks;

namespace DeathWish.MockClient.Core;

/// <summary>A single-use connection. Await each request before starting another.</summary>
public sealed class ConnectionTestClient : IDisposable
{
    private readonly TcpClient _client = new TcpClient();
    private readonly PacketReader _reader = new PacketReader();
    private readonly object _sync = new object();
    private readonly TaskCompletionSource<bool> _disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<byte[]>? _pendingResponse;
    private MessageType _expectedResponse;
    private bool _closed;
    private bool _connectStarted;
    private bool _connected;
    private Exception? _disconnectError;
    private readonly TaskCompletionSource<JoinAck> _admission = new TaskCompletionSource<JoinAck>(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? _acceptedRoom;
    private int? _requestedRoom;
    private bool _isSendingMovement;
    private WorldSnapshot? _latestSnapshot;

    /// <summary>Returns the latest immutable full snapshot, or null before the first snapshot. Safe to read from the UI thread.</summary>
    public WorldSnapshot? LatestSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _latestSnapshot;
            }
        }
    }

    /// <summary>Waits for actual player creation. Cancellation permanently closes the connection.</summary>
    public async Task<JoinAck> WaitForAdmissionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_acceptedRoom == null) throw new InvalidOperationException("Join a room successfully before waiting for admission.");
        }
        using (cancellationToken.Register(() => Close(new OperationCanceledException(cancellationToken))))
        {
            try
            {
                JoinAck ack = await _admission.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return ack;
            }
            catch { cancellationToken.ThrowIfCancellationRequested(); throw; }
        }
    }

    /// <summary>Completes when the connection closes, including local disposal. Never faults.</summary>
    public Task Disconnected => _disconnected.Task;
    public Exception? DisconnectError
    {
        get { lock (_sync) { return _disconnectError; } }
    }

    /// <summary>Cancellation closes the connection permanently.</summary>
    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_closed) throw new ObjectDisposedException(nameof(ConnectionTestClient));
            if (_connectStarted) throw new InvalidOperationException("Create a new client for each connection.");
            _connectStarted = true;
        }

        using (cancellationToken.Register(() => Close(new OperationCanceledException(cancellationToken))))
        {
            try
            {
                await _client.ConnectAsync(host, port).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (_closed) throw new IOException("Connection closed while connecting.", _disconnectError);
                    _client.NoDelay = true;
                    _connected = true;
                }
                _ = ReceiveAsync();
            }
            catch (Exception exception)
            {
                Close(exception);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }
    }

    /// <summary>Sends at most 4096 UTF-8 bytes including embedded LF. Cancellation closes the connection.</summary>
    public Task<string> EchoAsync(string message, CancellationToken cancellationToken)
    {
        byte[] payload = PacketCodec.EncodeEcho(MessageType.EchoRequest, message);
        return RequestAsync(payload, MessageType.EchoResponse, response =>
        {
            string echoed = PacketCodec.DecodeEcho(response);
            if (echoed != message) throw new InvalidDataException("Echo mismatch.");
            return echoed;
        }, cancellationToken);
    }

    /// <summary>Requests a positive room ID. Admission failures are returned normally. Cancellation closes the connection.</summary>
    public Task<JoinRoomResponse> JoinRoomAsync(int roomId, CancellationToken cancellationToken)
    {
        byte[] payload = PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(roomId));
        return RequestAsync(payload, MessageType.JoinRoomResponse, response =>
        {
            JoinRoomResponse result = PacketCodec.DecodeJoinRoomResponse(response);
            if (result.RoomId != roomId) throw new InvalidDataException("Room response does not match the requested room.");
            return result;
        }, cancellationToken);
    }

    /// <summary>Sends input after JoinAck without waiting for a response. Await all other sends/requests first; cancellation closes the connection.</summary>
    public async Task SendMoveAsync(MoveInput input, CancellationToken cancellationToken)
    {
        byte[] frame = PacketCodec.EncodeMoveInput(input);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_closed)
            {
                throw new IOException("Connection is closed.", _disconnectError);
            }
            if (!_connected || _admission.Task.Status != TaskStatus.RanToCompletion)
            {
                throw new InvalidOperationException("Wait for world admission before sending movement.");
            }
            if (_pendingResponse != null || _isSendingMovement)
            {
                throw new InvalidOperationException("A send or request is already pending.");
            }
            _isSendingMovement = true;
        }

        using (cancellationToken.Register(() => Close(new OperationCanceledException(cancellationToken))))
        {
            try
            {
                await _client.GetStream().WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                Close(exception);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            finally
            {
                lock (_sync)
                {
                    _isSendingMovement = false;
                }
            }
        }
    }

    private async Task<T> RequestAsync<T>(byte[] payload, MessageType expectedResponse,
        Func<byte[], T> decode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_closed) throw new IOException("Connection is closed.", _disconnectError);
            if (!_connected) throw new InvalidOperationException("Connect before sending a request.");
            if (_pendingResponse != null || _isSendingMovement)
            {
                throw new InvalidOperationException("A send or request is already pending.");
            }
            _expectedResponse = expectedResponse;
            _requestedRoom = expectedResponse == MessageType.JoinRoomResponse
                ? PacketCodec.DecodeJoinRoomRequest(payload.AsSpan(PacketCodec.HEADER_SIZE).ToArray()).RoomId : (int?)null;
            _pendingResponse = response;
        }

        using (cancellationToken.Register(() => Close(new OperationCanceledException(cancellationToken))))
        {
            try
            {
                NetworkStream stream = _client.GetStream();
                await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
                byte[] received = await response.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return decode(received);
            }
            catch (Exception exception)
            {
                Close(exception);
                // Observe the pending response even when the write itself failed.
                _ = response.Task.Exception;
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            finally
            {
                lock (_sync) { _pendingResponse = null; }
            }
        }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            NetworkStream stream = _client.GetStream();
            byte[] buffer = new byte[4096];


            while (true)
            {
                int count = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (count == 0)
                {
                    if (_reader.HasPartialFrame) throw new EndOfStreamException("Server closed an incomplete frame.");
                    Close(null);
                    return;
                }

                _reader.Feed(buffer.AsSpan(0, count), (type, payload) =>
                {
                    lock (_sync)
                    {
                        if (type == MessageType.WorldSnapshot)
                        {
                            WorldSnapshot snapshot = SnapshotCodec.Decode(payload);
                            bool hasAdmission = _admission.Task.Status == TaskStatus.RanToCompletion;
                            if (!hasAdmission || snapshot.RoomId != _acceptedRoom ||
                                snapshot.ServerTick < _admission.Task.Result.ServerTick ||
                                (_latestSnapshot != null && snapshot.ServerTick <= _latestSnapshot.ServerTick))
                            {
                                throw new InvalidDataException("Unexpected world snapshot.");
                            }
                            bool hasLocalPlayer = false;
                            foreach (SnapshotPlayer player in snapshot.Players)
                            {
                                hasLocalPlayer |= player.PlayerId == _admission.Task.Result.PlayerId;
                            }
                            if (!hasLocalPlayer)
                            {
                                throw new InvalidDataException("Snapshot is missing the admitted player.");
                            }
                            _latestSnapshot = snapshot;
                            return;
                        }
                        if (type == MessageType.JoinAck)
                        {
                            JoinAck ack = PacketCodec.DecodeJoinAck(payload);
                            if (_acceptedRoom != ack.RoomId || !_admission.TrySetResult(ack))
                                throw new InvalidDataException("Unexpected world admission.");
                            return;
                        }
                        if (_pendingResponse != null && type == _expectedResponse && type == MessageType.JoinRoomResponse)
                        {
                            JoinRoomResponse response = PacketCodec.DecodeJoinRoomResponse(payload);
                            if (response.RoomId != _requestedRoom) throw new InvalidDataException("Room response does not match the request.");
                            if (response.Result == RoomJoinResult.Success) _acceptedRoom = response.RoomId;
                        }
                        if (_pendingResponse == null || type != _expectedResponse || !_pendingResponse.TrySetResult(payload))
                            throw new InvalidDataException("Unexpected response.");
                    }
                });
            }
        }
        catch (Exception exception)
        {
            Close(exception);
        }
    }

    private void Close(Exception? error)
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            _disconnectError = error;
            _pendingResponse?.TrySetException(error ?? new EndOfStreamException("Connection closed."));
            _admission.TrySetException(error ?? new EndOfStreamException("Connection closed before admission."));
            _ = _admission.Task.Exception;
            _client.Dispose();
            _disconnected.TrySetResult(true);
        }
    }

    public void Dispose() => Close(null);
}
