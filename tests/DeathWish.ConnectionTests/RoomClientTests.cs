using System.Net;
using System.Net.Sockets;
using DeathWish.MockClient.Core;
using DeathWish.Protocol;
using DeathWish.Server;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Groups;
using DotNetty.Transport.Channels.Sockets;

static class RoomClientTests
{
    public static async Task RunAsync()
    {
        using (var unconnected = new ConnectionTestClient())
        {
            await Reject<InvalidOperationException>(() => unconnected.JoinRoomAsync(1, default));
            await Reject<InvalidDataException>(() => unconnected.JoinRoomAsync(0, default));
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var acceptGroup = new MultithreadEventLoopGroup(1);
        var workerGroup = new MultithreadEventLoopGroup(1);
        var connections = new DefaultChannelGroup(acceptGroup.GetNext());
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions);
        IChannel? listener = null;
        try
        {
            listener = await new ServerBootstrap().Group(acceptGroup, workerGroup)
                .Channel<TcpServerSocketChannel>()
                .ChildHandler(new ActionChannelInitializer<IChannel>(channel =>
                {
                    connections.Add(channel);
                    channel.Pipeline.AddLast(new GameChannelHandler(sessions, rooms));
                })).BindAsync(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)listener.LocalAddress).Port;
            using var first = new ConnectionTestClient();
            using var second = new ConnectionTestClient();
            await first.ConnectAsync("127.0.0.1", port, deadline.Token);
            await second.ConnectAsync("127.0.0.1", port, deadline.Token);
            await ExpectJoin(first, 99, RoomJoinResult.RoomNotFound, deadline.Token);
            await ExpectJoin(first, 1, RoomJoinResult.Success, deadline.Token);
            await ExpectJoin(first, 1, RoomJoinResult.Success, deadline.Token);
            await ExpectJoin(first, 2, RoomJoinResult.AlreadyInAnotherRoom, deadline.Token);
            await ExpectJoin(second, 1, RoomJoinResult.Success, deadline.Token);
            if (!rooms.TryGetRoom(1, out var room) || room.MemberCount != 2)
                throw new Exception("TCP clients did not share room 1");
            if (await first.EchoAsync("after join", deadline.Token) != "after join")
                throw new Exception("Echo after admission failed");

            await listener.CloseAsync();
            await connections.CloseAsync();
            await Task.WhenAll(first.Disconnected, second.Disconnected).WaitAsync(deadline.Token);
        }
        finally
        {
            if (listener is not null) await listener.CloseAsync();
            await connections.CloseAsync();
            await Task.WhenAll(acceptGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(2)),
                workerGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(2)));
        }
        if (sessions.Count != 0 || !rooms.TryGetRoom(1, out var emptied) || emptied.MemberCount != 0)
            throw new Exception("TCP shutdown left room members or sessions");

        await VerifyPeerResponses(deadline.Token);
        Console.WriteLine("PASS TCP room admission, shared membership, Echo coexistence, shutdown, and client response validation");
    }

    private static async Task VerifyPeerResponses(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            foreach (byte[] response in new[] {
                PacketCodec.EncodeJoinRoomResponse(new JoinRoomResponse(1, RoomJoinResult.Success)),
                PacketCodec.EncodeJoinRoomResponse(new JoinRoomResponse(2, RoomJoinResult.Success)),
                PacketCodec.EncodeEcho(MessageType.EchoResponse, "wrong type"),
                new byte[] { 0, 5, 4, 0, 0, 0, 1, 255 },
                new byte[] { 0, 4, 4, 0, 0, 0, 1 } })
            {
                using var client = new ConnectionTestClient();
                Task<TcpClient> accepting = listener.AcceptTcpClientAsync();
                await client.ConnectAsync("127.0.0.1", port, token);
                using TcpClient peer = await accepting.WaitAsync(token);
                Task<JoinRoomResponse> pending = client.JoinRoomAsync(1, token);
                byte[] request = new byte[7];
                await peer.GetStream().ReadExactlyAsync(request, token);
                if (!request.SequenceEqual(new byte[] { 0, 4, 3, 0, 0, 0, 1 })) throw new Exception("Wrong client request bytes");
                await Reject<InvalidOperationException>(() => client.EchoAsync("overlap", token));
                await Reject<InvalidOperationException>(() => client.JoinRoomAsync(2, token));
                await peer.GetStream().WriteAsync(response.AsMemory(0, 2), token);
                await peer.GetStream().WriteAsync(response.AsMemory(2), token);
                if (response.SequenceEqual(new byte[] { 0, 5, 4, 0, 0, 0, 1, 0 }))
                {
                    if ((await pending).Result != RoomJoinResult.Success) throw new Exception("Split response failed");
                }
                else
                {
                    await Reject<InvalidDataException>(() => pending);
                    await client.Disconnected.WaitAsync(token);
                }
            }

            for (int scenario = 0; scenario < 3; scenario++)
            {
                using var client = new ConnectionTestClient();
                Task<TcpClient> accepting = listener.AcceptTcpClientAsync();
                await client.ConnectAsync("127.0.0.1", port, token);
                using TcpClient peer = await accepting.WaitAsync(token);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task<JoinRoomResponse> pending = client.JoinRoomAsync(1, cancellation.Token);
                await peer.GetStream().ReadExactlyAsync(new byte[7], token);
                if (scenario == 0)
                {
                    cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
                    await Reject<OperationCanceledException>(() => pending);
                }
                else
                {
                    if (scenario == 2) await peer.GetStream().WriteAsync(new byte[] { 0, 5, 4, 0 }, token);
                    peer.Dispose();
                    await Reject<EndOfStreamException>(() => pending);
                }
                await client.Disconnected.WaitAsync(token);
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task ExpectJoin(ConnectionTestClient client, int id, RoomJoinResult expected, CancellationToken token)
    {
        JoinRoomResponse response = await client.JoinRoomAsync(id, token);
        if (response.RoomId != id || response.Result != expected) throw new Exception("Unexpected admission result");
    }

    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
