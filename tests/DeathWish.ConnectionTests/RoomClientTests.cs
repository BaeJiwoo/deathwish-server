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
        var clock = new ManualTimeProvider();
        var rooms = new RoomManager(sessions, 2, GameWorld.DEFAULT_ADMISSION_CAPACITY,
            GameWorld.DEFAULT_MAX_COMMANDS_PER_TICK, clock);
        using var worldCancellation = new CancellationTokenSource();
        var worldLoop = new WorldLoop(rooms.GetWorlds(), 10, clock);
        Task worldTask = worldLoop.RunAsync(worldCancellation.Token);
        IChannel? listener = null;
        try
        {
            await clock.NextDelayAsync();
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
            if (room.World.PlayerCount != 0) throw new Exception("TCP handler mutated world before tick");
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await clock.NextDelayAsync();
            PlayerState[] players = room.World.GetPlayers();
            JoinAck firstAck = await first.WaitForAdmissionAsync(deadline.Token);
            JoinAck secondAck = await second.WaitForAdmissionAsync(deadline.Token);
            if (firstAck.PlayerId == secondAck.PlayerId || firstAck.ServerTick != 1 || secondAck.ServerTick != 1 ||
                firstAck.RoomId != 1 || firstAck.X != 0 || firstAck.Y != 0 || firstAck.Z != 0)
                throw new Exception("TCP JoinAck does not match world admission");
            if (players.Length != 2 || players.Select(player => player.PlayerId).Distinct().Count() != 2 ||
                !players.Select(player => player.SessionId).Order().SequenceEqual(room.GetMemberIds().Order()))
                throw new Exception("TCP room admission did not create two distinct world players");
            if (await first.EchoAsync("after join", deadline.Token) != "after join")
                throw new Exception("Echo after admission failed");

            await first.SendMoveAsync(new MoveInput(0, 1, 0), deadline.Token);
            await first.EchoAsync("movement received", deadline.Token);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await clock.NextDelayAsync();
            players = room.World.GetPlayers();
            if (Math.Abs(players.Single(player => player.PlayerId == firstAck.PlayerId).Position.X - .5f) > .0001f ||
                players.Single(player => player.PlayerId == secondAck.PlayerId).Position != System.Numerics.Vector3.Zero)
            {
                throw new Exception("TCP movement did not apply exclusively to its sender");
            }

            while (first.LatestSnapshot?.ServerTick != 2 || second.LatestSnapshot?.ServerTick != 2)
            {
                await Task.Delay(1, deadline.Token);
            }
            if (!SnapshotCodec.Encode(first.LatestSnapshot!).SequenceEqual(SnapshotCodec.Encode(second.LatestSnapshot!)) ||
                first.LatestSnapshot!.Players.Count != 2 ||
                Math.Abs(first.LatestSnapshot.Players.Single(player => player.PlayerId == firstAck.PlayerId).X - .5f) > .0001f)
            {
                throw new Exception("TCP clients received different world snapshots.");
            }

            clock.Advance(TimeSpan.FromMilliseconds(200));
            await clock.NextDelayAsync();
            if (Math.Abs(room.World.GetPlayers().Single(player => player.PlayerId == firstAck.PlayerId).Position.X - .5f) > .0001f)
            {
                throw new Exception("Expired TCP input continued moving during catch-up");
            }

            for (uint sequence = 1; sequence <= 51; sequence++)
            {
                await first.SendMoveAsync(new MoveInput(sequence, 1, 0), deadline.Token);
                await first.EchoAsync("edge input received", deadline.Token);
                clock.Advance(TimeSpan.FromMilliseconds(200));
                await clock.NextDelayAsync();
            }

            var edgePosition = room.World.GetPlayers().Single(player => player.PlayerId == firstAck.PlayerId).Position;
            await first.SendMoveAsync(new MoveInput(52, 0, 0), deadline.Token);
            await first.EchoAsync("stop received", deadline.Token);
            clock.Advance(TimeSpan.FromMilliseconds(200));
            await clock.NextDelayAsync();
            long fallingTick = room.World.CurrentTick;
            while (first.LatestSnapshot?.ServerTick != fallingTick || second.LatestSnapshot?.ServerTick != fallingTick)
            {
                await Task.Delay(1, deadline.Token);
            }

            var falling = room.World.GetPlayers().Single(player => player.PlayerId == firstAck.PlayerId).Position;
            SnapshotPlayer observed = second.LatestSnapshot!.Players.Single(player => player.PlayerId == firstAck.PlayerId);
            if (edgePosition.Y >= 0 || falling.Y >= edgePosition.Y || MathF.Abs(falling.X - edgePosition.X) > .0001f ||
                observed.X != falling.X || observed.Y != falling.Y || observed.Z != falling.Z ||
                !SnapshotCodec.Encode(first.LatestSnapshot!).SequenceEqual(SnapshotCodec.Encode(second.LatestSnapshot!)))
            {
                throw new Exception("Both TCP clients must see the authoritative falling position after horizontal input stops.");
            }

            await listener.CloseAsync();
            await connections.CloseAsync();
            await Task.WhenAll(first.Disconnected, second.Disconnected).WaitAsync(deadline.Token);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await clock.NextDelayAsync();
            if (room.World.PlayerCount != 0 || room.World.Commands.AdmissionCount != 0)
                throw new Exception("TCP departure left world players or reservations");
        }
        finally
        {
            if (listener is not null) await listener.CloseAsync();
            await connections.CloseAsync();
            worldCancellation.Cancel();
            await worldTask.WaitAsync(deadline.Token);
            await Task.WhenAll(acceptGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(2)),
                workerGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(2)));
        }
        if (sessions.Count != 0 || !rooms.TryGetRoom(1, out var emptied) || emptied.MemberCount != 0 || emptied.World.PlayerCount != 0)
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
                PacketCodec.EncodeJoinRoomResponse(new JoinRoomResponse(1, RoomJoinResult.WorldUnavailable)),
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
                if (response.SequenceEqual(new byte[] { 0, 5, 4, 0, 0, 0, 1, 0 }) ||
                    response.SequenceEqual(new byte[] { 0, 5, 4, 0, 0, 0, 1, 4 }))
                {
                    if ((await pending).Result != (RoomJoinResult)response[^1]) throw new Exception("Split response failed");
                    if (client.Disconnected.IsCompleted) throw new Exception("Valid result closed the client");
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
