using System.Net;
using System.Net.Sockets;
using System.Numerics;
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
            var secondSpawn = new System.Numerics.Vector3(secondAck.X, secondAck.Y, secondAck.Z);
            if (secondSpawn.Length() < 1 || System.Numerics.Vector3.Distance(secondSpawn, players.Single(player => player.PlayerId == secondAck.PlayerId).Position) > .001f)
            {
                throw new Exception("Second spawn must be separate and match its announced position");
            }
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
                System.Numerics.Vector3.Distance(players.Single(player => player.PlayerId == secondAck.PlayerId).Position, secondSpawn) > .001f)
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
        await VerifyLatencyReplies(deadline.Token);
        await VerifyArenaShotBuffer(deadline.Token);
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

    private static async Task VerifyLatencyReplies(CancellationToken token)
    {
        const int ROOM_ID = 1;
        const int PLAYER_ID = 2;
        const long FIRST_PROBE_TOKEN = 10;
        const long SECOND_PROBE_TOKEN = 11;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new ConnectionTestClient();
            Task<TcpClient> accepting = listener.AcceptTcpClientAsync();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await client.ConnectAsync("127.0.0.1", port, token);
            using TcpClient peer = await accepting.WaitAsync(token);
            NetworkStream stream = peer.GetStream();

            Task<JoinRoomResponse> joining = client.JoinMatchAsync(ROOM_ID, "Tester", token);
            byte[] expectedJoin = MatchCodec.EncodeJoinRequest(ROOM_ID, "Tester");
            if (!(await ReadFrameAsync(stream, token)).SequenceEqual(expectedJoin))
            {
                throw new Exception("Named TCP join bytes differ.");
            }
            byte[] joined = PacketCodec.EncodeJoinRoomResponse(new JoinRoomResponse(ROOM_ID, RoomJoinResult.Success));
            byte[] ack = PacketCodec.EncodeJoinAck(new JoinAck(ROOM_ID, PLAYER_ID, 1, 0, 0, 0, WorldMapId.TeamDeathmatch));
            await stream.WriteAsync(joined.Concat(ack).ToArray(), token);
            if ((await joining).Result != RoomJoinResult.Success || (await client.WaitForAdmissionAsync(token)).PlayerId != PLAYER_ID)
            {
                throw new Exception("Named TCP admission failed.");
            }

            Task<string> echo = client.EchoAsync("in flight", token);
            byte[] echoRequest = PacketCodec.EncodeEcho(MessageType.EchoRequest, "in flight");
            if (!(await ReadFrameAsync(stream, token)).SequenceEqual(echoRequest))
            {
                throw new Exception("Wrong echo request during latency check.");
            }
            var player = new MatchPlayer(PLAYER_ID, "Tester", TeamId.Red, MatchState.MAX_HEALTH, 0, 0, 0, 5);
            var state = new MatchState(ROOM_ID, 2, MatchPhase.Waiting, 0, 0, null, new[] { player });
            byte[] probe = PingCodec.EncodeProbe(FIRST_PROBE_TOKEN);
            byte[] incoming = probe.Concat(MatchCodec.EncodeState(state))
                .Concat(PacketCodec.EncodeEcho(MessageType.EchoResponse, "in flight")).ToArray();
            int split = PacketCodec.HEADER_SIZE + 1;
            await stream.WriteAsync(incoming.AsMemory(0, split), token);
            await stream.WriteAsync(incoming.AsMemory(split), token);
            byte[] firstReply = await ReadFrameAsync(stream, token);
            if (!firstReply.SequenceEqual(PingCodec.EncodeReply(FIRST_PROBE_TOKEN)) ||
                await echo != "in flight" || client.LatestMatchState?.Players.Single().PingMs != 5)
            {
                throw new Exception("Fragmented latency probe interfered with a roster or pending Echo.");
            }

            var movement = new MoveInput(0, 1, 0);
            await stream.WriteAsync(PingCodec.EncodeProbe(SECOND_PROBE_TOKEN), token);
            Task sendingMovement = client.SendMoveAsync(movement, token);
            byte[] first = await ReadFrameAsync(stream, token);
            byte[] second = await ReadFrameAsync(stream, token);
            await sendingMovement;
            byte[] moveFrame = PacketCodec.EncodeMoveInput(movement);
            byte[] replyFrame = PingCodec.EncodeReply(SECOND_PROBE_TOKEN);
            bool framesAreIntact = (first.SequenceEqual(moveFrame) && second.SequenceEqual(replyFrame)) ||
                (second.SequenceEqual(moveFrame) && first.SequenceEqual(replyFrame));
            if (!framesAreIntact)
            {
                throw new Exception("Latency reply and movement bytes interleaved.");
            }
            Console.WriteLine("PASS TCP latency replies coexist with fragmented match frames, pending Echo, and movement");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task VerifyArenaShotBuffer(CancellationToken token)
    {
        const int ROOM_ID = 1;
        const int PLAYER_ID = 2;
        const int EXPECTED_BUFFER_SIZE = 64;
        const int SHOT_COUNT = EXPECTED_BUFFER_SIZE + 2;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new ConnectionTestClient();
            Task<TcpClient> accepting = listener.AcceptTcpClientAsync();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await client.ConnectAsync("127.0.0.1", port, token);
            using TcpClient peer = await accepting.WaitAsync(token);
            NetworkStream stream = peer.GetStream();

            Task<JoinRoomResponse> joining = client.JoinMatchAsync(ROOM_ID, "Viewer", receiveArenaShots: true, token);
            byte[] expectedJoin = MatchCodec.EncodeJoinRequest(ROOM_ID, "Viewer", receiveArenaShots: true);
            if (!(await ReadFrameAsync(stream, token)).SequenceEqual(expectedJoin))
            {
                throw new Exception("TCP join did not opt in to firing events.");
            }
            byte[] joined = PacketCodec.EncodeJoinRoomResponse(new JoinRoomResponse(ROOM_ID, RoomJoinResult.Success));
            byte[] ack = PacketCodec.EncodeJoinAck(new JoinAck(ROOM_ID, PLAYER_ID, 1, 0, 0, 0, WorldMapId.TeamDeathmatch));
            await stream.WriteAsync(joined.Concat(ack).ToArray(), token);
            await joining;
            await client.WaitForAdmissionAsync(token);

            Task<string> echo = client.EchoAsync("firing complete", token);
            await ReadFrameAsync(stream, token);
            IEnumerable<byte> shots = Enumerable.Range(2, SHOT_COUNT).SelectMany(tick => MatchCodec.EncodeShot(
                new ArenaShot(ROOM_ID, tick, PLAYER_ID, Vector3.Zero, Vector3.UnitZ, ArenaShotOutcome.Miss)));
            byte[] incoming = shots.Concat(ActionCodec.EncodeResult("Raycast miss"))
                .Concat(PacketCodec.EncodeEcho(MessageType.EchoResponse, "firing complete")).ToArray();
            await stream.WriteAsync(incoming, token);
            await echo;

            for (int index = 0; index < EXPECTED_BUFFER_SIZE; index++)
            {
                if (!client.TryReadArenaShot(out ArenaShot shot) || shot.ServerTick != index + 4)
                {
                    throw new Exception("The firing queue did not retain the latest events in receive order.");
                }
            }
            if (client.TryReadArenaShot(out _) || !client.TryReadRaycastResult(out string? ray) || ray != "Raycast miss")
            {
                throw new Exception("Arena firing events changed the legacy raycast response stream or exceeded the buffer limit.");
            }
            Console.WriteLine("PASS TCP arena opt-in and bounded firing events coexist with legacy raycast results");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[PacketCodec.HEADER_SIZE];
        await stream.ReadExactlyAsync(header, token);
        int payloadSize = (header[0] << 8) | header[1];
        var frame = new byte[header.Length + payloadSize];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(header.Length), token);
        return frame;
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
