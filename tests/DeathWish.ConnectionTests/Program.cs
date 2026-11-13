using System.Net;
using System.Net.Sockets;
using DeathWish.Protocol;
using DeathWish.Server;
using DeathWish.MockClient.Core;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using DotNetty.Transport.Channels.Groups;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
}

static GameChannelHandler CreateHandler()
{
    var sessions = new SessionManager();
    return new GameChannelHandler(sessions, new RoomManager(sessions));
}

static void VerifyEcho(byte[] request, string expected, int split)
{
    var channel = new EmbeddedChannel(CreateHandler());
    channel.WriteInbound(Unpooled.WrappedBuffer(request[..split]));
    Check(channel.ReadOutbound<IByteBuffer>() == null, "Partial frame waits");
    channel.WriteInbound(Unpooled.WrappedBuffer(request[split..]));
    IByteBuffer response = channel.ReadOutbound<IByteBuffer>();
    try
    {
        byte[] bytes = new byte[response.ReadableBytes];
        response.ReadBytes(bytes);
        Check(bytes.SequenceEqual(PacketCodec.EncodeEcho(MessageType.EchoResponse, expected)), "Exact Echo bytes");
    }
    finally { response.Release(); channel.FinishAndReleaseAll(); }
}

var sessions = new SessionManager();
var rooms = new RoomManager(sessions);
var firstHandler = new GameChannelHandler(sessions, rooms);
var secondHandler = new GameChannelHandler(sessions, rooms);
Check(firstHandler.Session is null, "Session does not exist before activation");
Check(sessions.Count == 0 && !sessions.TryGetSession(Guid.NewGuid(), out _), "Empty registry rejects unknown ID");
var firstChannel = new EmbeddedChannel(firstHandler);
var secondChannel = new EmbeddedChannel(secondHandler);
try
{
    ClientSession firstSession = firstHandler.Session ?? throw new Exception("Missing first session");
    ClientSession secondSession = secondHandler.Session ?? throw new Exception("Missing second session");
    Check(firstSession.SessionId != Guid.Empty && firstSession.SessionId != secondSession.SessionId, "Connections receive distinct session IDs");
    Check(ReferenceEquals(firstSession.Channel, firstChannel) && ReferenceEquals(secondSession.Channel, secondChannel), "Sessions retain their own channel");
    Check(!firstSession.IsDisconnected && !secondSession.IsDisconnected, "New sessions are connected");
    Check(sessions.Count == 2, "Both connections registered");
    Check(sessions.TryGetSession(firstSession.SessionId, out var found) && ReferenceEquals(found, firstSession), "Lookup returns matching session");
    Check(rooms.TryGetRoom(1, out var room1) && rooms.TryGetRoom(2, out _), "Test rooms exist");
    Check(rooms.Join(Guid.NewGuid(), 1) == RoomJoinResult.SessionUnavailable, "Unknown session cannot join");
    Check(rooms.Join(firstSession.SessionId, 99) == RoomJoinResult.RoomNotFound && rooms.GetRoomId(firstSession.SessionId) is null, "Missing room leaves session unassigned");
    Check(rooms.Join(firstSession.SessionId, 1) == RoomJoinResult.Success, "First room admission succeeds");
    Check(rooms.Join(firstSession.SessionId, 1) == RoomJoinResult.Success && room1!.MemberCount == 1, "Same room admission is idempotent");
    Check(rooms.Join(firstSession.SessionId, 2) == RoomJoinResult.AlreadyInAnotherRoom && rooms.GetRoomId(firstSession.SessionId) == 1, "Room switch rejected without changing membership");
    Check(rooms.Join(secondSession.SessionId, 1) == RoomJoinResult.Success && room1!.MemberCount == 2, "Two sessions share one room");
    Guid[] snapshot = room1!.GetMemberIds();
    snapshot[0] = Guid.Empty;
    Check(!room1.GetMemberIds().Contains(Guid.Empty), "Room members cannot be modified through snapshot");
    Check(rooms.Leave(secondSession.SessionId) && !rooms.Leave(secondSession.SessionId), "Repeated departure is safe");
    Check(rooms.Join(secondSession.SessionId, 2) == RoomJoinResult.Success, "Session may join after explicit departure");

    firstChannel.Pipeline.FireChannelActive();
    Check(ReferenceEquals(firstSession, firstHandler.Session), "Repeated activation preserves session identity");
    Check(sessions.Count == 2, "Repeated activation does not duplicate registration");
    await firstChannel.CloseAsync();
    Check(firstSession.IsDisconnected, "Channel close marks session disconnected");
    Check(sessions.Count == 1 && !sessions.TryGetSession(firstSession.SessionId, out _), "Closed session removed from registry");
    Check(room1.MemberCount == 0 && rooms.GetRoomId(firstSession.SessionId) is null, "Disconnect removes room membership");
    Check(rooms.Join(firstSession.SessionId, 1) == RoomJoinResult.SessionUnavailable, "Disconnected session cannot rejoin");
    Check(rooms.TryGetRoom(1, out var emptyRoom) && ReferenceEquals(room1, emptyRoom), "Empty room is retained");
    Check(!secondSession.IsDisconnected && secondChannel.Active, "Other session remains connected");
    await firstChannel.CloseAsync();
    firstChannel.Pipeline.FireChannelInactive();
    Check(firstSession.IsDisconnected && ReferenceEquals(firstSession, firstHandler.Session), "Repeated disconnect preserves terminal session state");
    Check(sessions.Count == 1 && sessions.TryGetSession(secondSession.SessionId, out _), "Repeated removal preserves other session");

    secondChannel.WriteInbound(Unpooled.WrappedBuffer(new byte[] { 0, 0, 99 }));
    secondChannel.RunPendingTasks();
    Check(secondSession.IsDisconnected && !secondChannel.Active, "Protocol error closes its session");
    Check(sessions.Count == 0, "Protocol error removes session from registry");
    Check(rooms.TryGetRoom(2, out var room2) && room2.MemberCount == 0, "Protocol error removes room membership");
}
finally
{
    firstChannel.FinishAndReleaseAll();
    secondChannel.FinishAndReleaseAll();
}

var shutdownHandlers = Enumerable.Range(0, 8).Select(_ => new GameChannelHandler(sessions, rooms)).ToArray();
// Default embedded IDs are shared; a channel group requires distinct channel IDs.
var shutdownChannels = shutdownHandlers.Select(handler => new EmbeddedChannel(new TestChannelId(), false, handler)).ToArray();
var connections = new DefaultChannelGroup(shutdownChannels[0].EventLoop);
try
{
    foreach (var channel in shutdownChannels) connections.Add(channel);
    Check(connections.Count == 8, "Shutdown group contains all channels");
    Check(sessions.Count == 8, "Shutdown registry contains all connections");
    foreach (var handler in shutdownHandlers)
        Check(rooms.Join(handler.Session!.SessionId, 1) == RoomJoinResult.Success, "Shutdown session joins room");
    await connections.CloseAsync();
    // Embedded event loops need an explicit drain for queued inactive notifications.
    foreach (var channel in shutdownChannels) channel.RunPendingTasks();
    Check(sessions.Count == 0 && shutdownHandlers.All(handler => handler.Session!.IsDisconnected), "Group shutdown removes all sessions");
    Check(rooms.TryGetRoom(1, out var shutdownRoom) && shutdownRoom.MemberCount == 0, "Group shutdown empties room");
}
finally
{
    foreach (var channel in shutdownChannels) channel.FinishAndReleaseAll();
}

Parallel.For(0, 32, _ =>
{
    var handler = new GameChannelHandler(sessions, rooms);
    var channel = new EmbeddedChannel(handler);
    try
    {
        if (!sessions.TryGetSession(handler.Session!.SessionId, out var registered) || !ReferenceEquals(registered, handler.Session))
            throw new Exception("Concurrent registration lookup failed");
    }
    finally { channel.FinishAndReleaseAll(); }
});
Check(sessions.Count == 0, "Concurrent connection lifecycles leave registry empty");

await RoomTests.RunAsync();
RoomProtocolTests.Run();
RoomHandlerTests.Run();

Check(PacketCodec.EncodeEcho(MessageType.EchoRequest, "Hi").SequenceEqual(new byte[] { 0, 2, 1, 72, 105 }), "Wire layout");
string text = "\uD55C\uAE00\nembedded\0";
byte[] frame = PacketCodec.EncodeEcho(MessageType.EchoRequest, text);
for (int split = 1; split < frame.Length; split++) VerifyEcho(frame, text, split);
VerifyEcho(PacketCodec.EncodeEcho(MessageType.EchoRequest, ""), "", 1);
VerifyEcho(PacketCodec.EncodeEcho(MessageType.EchoRequest, new string('a', 4096)), new string('a', 4096), 3);

var reader = new PacketReader();
int packets = 0;
byte[] combined = PacketCodec.EncodeEcho(MessageType.EchoResponse, "").Concat(PacketCodec.EncodeEcho(MessageType.EchoResponse, text)).ToArray();
foreach (byte value in combined)
    reader.Feed(new byte[] { value }, (type, payload) => { Check(type == MessageType.EchoResponse, "Response type"); packets++; });
Check(packets == 2 && !reader.HasPartialFrame, "Byte-by-byte frames including empty payload");
packets = 0;
reader.Feed(combined, (_, _) => packets++);
Check(packets == 2, "Combined frames");

foreach (byte[] invalid in new[] { new byte[] { 16, 1, 1 }, new byte[] { 0, 0, 99 }, new byte[] { 0, 1, 1, 255 }, new byte[] { 0, 0, 2 } })
{
    var channel = new EmbeddedChannel(CreateHandler());
    channel.WriteInbound(Unpooled.WrappedBuffer(invalid));
    channel.RunPendingTasks();
    Check(!channel.Active, "Invalid length/type/UTF8/direction closes connection");
    channel.FinishAndReleaseAll();
}
using (var client = new ConnectionTestClient())
{
    try { await client.EchoAsync("early", default); throw new Exception("Missing state check"); }
    catch (InvalidOperationException) { Console.WriteLine("PASS Echo before connect rejected"); }
    client.Dispose();
    await client.Disconnected.WaitAsync(TimeSpan.FromSeconds(1));
    try { await client.ConnectAsync("127.0.0.1", 1, default); throw new Exception("Missing disposal check"); }
    catch (ObjectDisposedException) { Console.WriteLine("PASS Connect after disposal rejected"); }
}

var listener = new TcpListener(IPAddress.Loopback, 0);
try { listener.Start(); }
catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AccessDenied)
{
    Console.WriteLine("SKIP TCP lifecycle tests: sandbox denied listening.");
    return 2;
}

try
{
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var client = new ConnectionTestClient();
    Task<TcpClient> accept = listener.AcceptTcpClientAsync();
    await client.ConnectAsync("127.0.0.1", port, deadline.Token);
    using TcpClient peer = await accept.WaitAsync(deadline.Token);
    try { await client.ConnectAsync("127.0.0.1", port, deadline.Token); throw new Exception("Missing duplicate check"); }
    catch (InvalidOperationException) { Console.WriteLine("PASS Duplicate connect rejected"); }
    peer.Dispose();
    await client.Disconnected.WaitAsync(deadline.Token);
    Check(client.DisconnectError == null, "Idle server closure detected");

    using var waitingClient = new ConnectionTestClient();
    accept = listener.AcceptTcpClientAsync();
    await waitingClient.ConnectAsync("127.0.0.1", port, deadline.Token);
    using TcpClient silentPeer = await accept.WaitAsync(deadline.Token);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    try { await waitingClient.EchoAsync("timeout", timeout.Token); throw new Exception("Missing timeout"); }
    catch (OperationCanceledException) { Console.WriteLine("PASS Echo timeout"); }
    await waitingClient.Disconnected.WaitAsync(deadline.Token);
    await RoomClientTests.RunAsync();
}
catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AccessDenied)
{
    Console.WriteLine("SKIP TCP lifecycle tests: sandbox denied connection.");
    return 2;
}
finally { listener.Stop(); }
return 0;

sealed class TestChannelId : IChannelId
{
    private readonly string _value = Guid.NewGuid().ToString("N");

    public string AsShortText() => _value;
    public string AsLongText() => _value;
    public int CompareTo(IChannelId? other) => other is null ? 1 : string.CompareOrdinal(_value, other.AsLongText());
}
