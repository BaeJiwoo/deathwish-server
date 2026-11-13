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

    private async Task<T> RequestAsync<T>(byte[] payload, MessageType expectedResponse,
        Func<byte[], T> decode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_closed) throw new IOException("Connection is closed.", _disconnectError);
            if (!_connected) throw new InvalidOperationException("Connect before sending a request.");
            if (_pendingResponse != null) throw new InvalidOperationException("A request is already pending.");
            _expectedResponse = expectedResponse;
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
            _client.Dispose();
            _disconnected.TrySetResult(true);
        }
    }

    public void Dispose() => Close(null);
}
