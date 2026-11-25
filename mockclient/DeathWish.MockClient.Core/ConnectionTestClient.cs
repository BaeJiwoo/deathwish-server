using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DeathWish.Protocol;

namespace DeathWish.MockClient.Core;

/// <summary>A single-use connection. Await each request before starting another.</summary>
public sealed class ConnectionTestClient : IDisposable
{
    private const int MAX_RAYCAST_RESULTS = 64;
    private const int MAX_PENDING_KILLS = 64;
    private const int MAX_PENDING_SHOTS = 64;
    private const int LATENCY_REPLY_TIMEOUT_MS = 5000;

    private readonly TcpClient _client = new TcpClient();
    private readonly PacketReader _reader = new PacketReader();
    private readonly object _sync = new object();
    private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
    private readonly TaskCompletionSource<bool> _disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _raycastResults = new Queue<string>();
    private readonly Queue<KillEvent> _killEvents = new Queue<KillEvent>();
    private readonly Queue<ArenaShot> _arenaShots = new Queue<ArenaShot>();
    private TaskCompletionSource<byte[]>? _pendingResponse;
    private MessageType _expectedResponse;
    private bool _closed;
    private bool _connectStarted;
    private bool _connected;
    private Exception? _disconnectError;
    private readonly TaskCompletionSource<JoinAck> _admission = new TaskCompletionSource<JoinAck>(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? _acceptedRoom;
    private int? _requestedRoom;
    private bool _requestedArenaShots;
    private bool _receivesArenaShots;
    private bool _isSendingInput;
    private WorldSnapshot? _latestSnapshot;
    private MatchState? _latestMatchState;
    private long _lastKillTick;
    private long _lastShotTick;

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

    /// <summary>Returns the latest immutable arena state, or null before its first update. Safe to read from the UI thread.</summary>
    public MatchState? LatestMatchState
    {
        get
        {
            lock (_sync)
            {
                return _latestMatchState;
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
        return RequestJoinAsync(roomId, payload, cancellationToken);
    }

    /// <summary>Requests admission with a validated nickname. Await actual world admission separately.</summary>
    public Task<JoinRoomResponse> JoinMatchAsync(int roomId, string nickname, CancellationToken cancellationToken)
    {
        return JoinMatchAsync(roomId, nickname, false, cancellationToken);
    }

    /// <summary>Requests named admission with optional arena shot events. The first accepted join fixes the capability.</summary>
    public Task<JoinRoomResponse> JoinMatchAsync(int roomId, string nickname, bool receiveArenaShots, CancellationToken cancellationToken)
    {
        byte[] payload = MatchCodec.EncodeJoinRequest(roomId, nickname, receiveArenaShots);
        return RequestJoinAsync(roomId, payload, cancellationToken, receiveArenaShots);
    }

    private Task<JoinRoomResponse> RequestJoinAsync(int roomId, byte[] payload, CancellationToken cancellationToken,
        bool receiveArenaShots = false)
    {
        return RequestAsync(payload, MessageType.JoinRoomResponse, response =>
        {
            JoinRoomResponse result = PacketCodec.DecodeJoinRoomResponse(response);
            if (result.RoomId != roomId) throw new InvalidDataException("Room response does not match the requested room.");
            return result;
        }, cancellationToken, roomId, receiveArenaShots);
    }

    /// <summary>Sends input after JoinAck without waiting for a response. Await all other sends/requests first; cancellation closes the connection.</summary>
    public Task SendMoveAsync(MoveInput input, CancellationToken cancellationToken) =>
        SendInputAsync(PacketCodec.EncodeMoveInput(input), cancellationToken);

    /// <summary>Sends a jump after admission. Await other sends and requests first. Cancellation closes the connection.</summary>
    public Task SendJumpAsync(CancellationToken cancellationToken) =>
        SendInputAsync(ActionCodec.EncodeJump(), cancellationToken);

    /// <summary>Sends a camera ray after admission. Read the response with TryReadRaycastResult. Cancellation closes the connection.</summary>
    public Task SendRayAsync(CameraRay ray, CancellationToken cancellationToken) =>
        SendInputAsync(ActionCodec.EncodeRay(ray), cancellationToken);

    /// <summary>Removes the oldest available raycast response. Safe to call from the UI thread.</summary>
    public bool TryReadRaycastResult(out string? result)
    {
        lock (_sync)
        {
            return _raycastResults.TryDequeue(out result);
        }
    }

    /// <summary>Removes the oldest buffered kill. Safe to call from the UI thread.</summary>
    public bool TryReadKillEvent(out KillEvent kill)
    {
        lock (_sync)
        {
            return _killEvents.TryDequeue(out kill);
        }
    }

    /// <summary>Removes the oldest buffered accepted shot. The buffer drops oldest events when full. Safe to call from the UI thread.</summary>
    public bool TryReadArenaShot(out ArenaShot shot)
    {
        lock (_sync)
        {
            return _arenaShots.TryDequeue(out shot);
        }
    }

    private async Task SendInputAsync(byte[] frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_closed)
            {
                throw new IOException("Connection is closed.", _disconnectError);
            }
            if (!_connected || _admission.Task.Status != TaskStatus.RanToCompletion)
            {
                throw new InvalidOperationException("Wait for world admission before sending input.");
            }
            if (_pendingResponse != null || _isSendingInput)
            {
                throw new InvalidOperationException("A send or request is already pending.");
            }
            _isSendingInput = true;
        }

        using (cancellationToken.Register(() => Close(new OperationCanceledException(cancellationToken))))
        {
            try
            {
                await WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
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
                    _isSendingInput = false;
                }
            }
        }
    }

    private async Task<T> RequestAsync<T>(byte[] payload, MessageType expectedResponse,
        Func<byte[], T> decode, CancellationToken cancellationToken, int? requestedRoomId = null,
        bool receiveArenaShots = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_closed) throw new IOException("Connection is closed.", _disconnectError);
            if (!_connected) throw new InvalidOperationException("Connect before sending a request.");
            if (_pendingResponse != null || _isSendingInput)
            {
                throw new InvalidOperationException("A send or request is already pending.");
            }
            _expectedResponse = expectedResponse;
            _requestedRoom = requestedRoomId;
            _requestedArenaShots = receiveArenaShots;
            _pendingResponse = response;
        }

        using (cancellationToken.Register(() => Close(new OperationCanceledException(cancellationToken))))
        {
            try
            {
                await WriteFrameAsync(payload, cancellationToken).ConfigureAwait(false);
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

    private async Task WriteFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        // A probe reply can run alongside one user operation, but their bytes cannot share a write.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _client.GetStream().WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
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

                long? latestProbe = null;
                _reader.Feed(buffer.AsSpan(0, count), (type, payload) =>
                {
                    lock (_sync)
                    {
                        if (type == MessageType.LatencyProbe)
                        {
                            RequireArenaAdmission();
                            // The server only measures its newest outstanding probe.
                            latestProbe = PingCodec.DecodeProbe(payload);
                            return;
                        }
                        if (type == MessageType.MatchState)
                        {
                            ReceiveMatchState(payload);
                            return;
                        }
                        if (type == MessageType.KillEvent)
                        {
                            ReceiveKillEvent(payload);
                            return;
                        }
                        if (type == MessageType.ArenaShot)
                        {
                            ReceiveArenaShot(payload);
                            return;
                        }
                        if (type == MessageType.RaycastResult)
                        {
                            if (_admission.Task.Status != TaskStatus.RanToCompletion || _raycastResults.Count >= MAX_RAYCAST_RESULTS)
                            {
                                throw new InvalidDataException("Unexpected raycast result.");
                            }
                            _raycastResults.Enqueue(PacketCodec.DecodeEcho(payload));
                            return;
                        }
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
                            if (response.Result == RoomJoinResult.Success && !_acceptedRoom.HasValue)
                            {
                                _acceptedRoom = response.RoomId;
                                _receivesArenaShots = _requestedArenaShots;
                            }
                        }
                        if (_pendingResponse == null || type != _expectedResponse || !_pendingResponse.TrySetResult(payload))
                            throw new InvalidDataException("Unexpected response.");
                    }
                });
                if (latestProbe is long token)
                {
                    using var timeout = new CancellationTokenSource(LATENCY_REPLY_TIMEOUT_MS);
                    await WriteFrameAsync(PingCodec.EncodeReply(token), timeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            Close(exception);
        }
    }

    // Called only by the receive loop under the connection lock.
    private void ReceiveMatchState(byte[] payload)
    {
        MatchState state = MatchCodec.DecodeState(payload);
        JoinAck admission = ValidateMatchMessage(state.RoomId, state.ServerTick);
        if (_latestMatchState != null && state.ServerTick <= _latestMatchState.ServerTick)
        {
            throw new InvalidDataException("Match state tick did not advance.");
        }

        foreach (MatchPlayer player in state.Players)
        {
            if (player.PlayerId == admission.PlayerId)
            {
                _latestMatchState = state;
                return;
            }
        }
        throw new InvalidDataException("Match state is missing the admitted player.");
    }

    // Called only by the receive loop under the connection lock.
    private void ReceiveKillEvent(byte[] payload)
    {
        KillEvent kill = MatchCodec.DecodeKill(payload);
        ValidateMatchMessage(kill.RoomId, kill.ServerTick);
        if (kill.ServerTick < _lastKillTick)
        {
            throw new InvalidDataException("Kill event tick went backwards.");
        }

        _lastKillTick = kill.ServerTick;
        if (_killEvents.Count == MAX_PENDING_KILLS)
        {
            _killEvents.Dequeue();
        }
        _killEvents.Enqueue(kill);
    }

    // Called only by the receive loop under the connection lock.
    private void ReceiveArenaShot(byte[] payload)
    {
        if (!_receivesArenaShots)
        {
            throw new InvalidDataException("Unexpected arena shot event.");
        }

        ArenaShot shot = MatchCodec.DecodeShot(payload);
        ValidateMatchMessage(shot.RoomId, shot.ServerTick);
        if (shot.ServerTick < _lastShotTick)
        {
            throw new InvalidDataException("Arena shot tick went backwards.");
        }

        _lastShotTick = shot.ServerTick;
        if (_arenaShots.Count == MAX_PENDING_SHOTS)
        {
            _arenaShots.Dequeue();
        }
        _arenaShots.Enqueue(shot);
    }

    private JoinAck ValidateMatchMessage(int roomId, long serverTick)
    {
        JoinAck admission = RequireArenaAdmission();
        if (roomId != admission.RoomId || serverTick < admission.ServerTick)
        {
            throw new InvalidDataException("Match message does not match world admission.");
        }
        return admission;
    }

    private JoinAck RequireArenaAdmission()
    {
        if (_admission.Task.Status != TaskStatus.RanToCompletion)
        {
            throw new InvalidDataException("Match message before world admission.");
        }

        JoinAck admission = _admission.Task.Result;
        if (admission.MapId != WorldMapId.TeamDeathmatch)
        {
            throw new InvalidDataException("Match message outside the arena.");
        }
        return admission;
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
