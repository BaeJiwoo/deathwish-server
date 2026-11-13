using System.Net;
using System.Net.Sockets;
using DeathWish.Protocol;
using DeathWish.Server;
using DeathWish.MockClient.Core;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
}

static void VerifyEcho(byte[] request, string expected, int split)
{
    var channel = new EmbeddedChannel(new GameChannelHandler());
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
    var channel = new EmbeddedChannel(new GameChannelHandler());
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
}
catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AccessDenied)
{
    Console.WriteLine("SKIP TCP lifecycle tests: sandbox denied connection.");
    return 2;
}
finally { listener.Stop(); }
return 0;
