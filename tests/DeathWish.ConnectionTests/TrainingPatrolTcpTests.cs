using System.Net;
using System.Net.Sockets;
using System.Numerics;
using DeathWish.MockClient.Core;
using DeathWish.Protocol;
using DeathWish.Server;
using DeathWish.Server.Physics;

internal static class TrainingPatrolTcpTests
{
    private const int ROOM_ID = 1;
    private const int CENTER_ROUTE = 1;
    private const int BRIDGE_ROUTE = 2;
    private const int JUMP_ROUTE = 3;
    private const int UPDATE_INTERVAL_MS = 33;
    private const int SHOT_INTERVAL_MS = 250;
    private const int TEST_TIMEOUT_SECONDS = 90;
    private const float WAYPOINT_TOLERANCE_METERS = 0.28f;
    private const float SHOT_LEAD_SECONDS = 0.045f;

    internal static void VerifyApproaches()
    {
        MeshWorldMap map = MeshWorldMap.Load(WorldMapId.TeamDeathmatch);
        PatrolNavigation navigation = PatrolNavigation.Load();
        foreach (MapSpawn spawn in map.Spawns)
        {
            foreach (PatrolRoute route in PatrolMode.Routes)
            {
                if (route.IsCombatDrill)
                {
                    for (int slot = 0; slot < MatchState.MAX_TEAM_SIZE; slot++)
                    {
                        Vector2 post = CombatDrill.GetPost((TeamId)spawn.TeamId, slot);
                        Require(navigation.FindApproach(new Vector2(spawn.Position.X, spawn.Position.Z), post).Length > 0,
                            $"{spawn.Id} can reach firing lane post {slot + 1}");
                    }
                    continue;
                }

                var (entry, _) = route.GetCircuit((TeamId)spawn.TeamId);
                Vector2[] approach = navigation.FindApproach(new Vector2(spawn.Position.X, spawn.Position.Z),
                    new Vector2(entry.X, entry.Z));
                Require(approach.Length > 0, $"{spawn.Id} can reach the entrance to patrol {route.Number}");
            }
        }
        Console.WriteLine("PASS all team spawns have a ground approach to each training patrol");
    }

    internal static async Task RunAsync()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        using var serverStop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TEST_TIMEOUT_SECONDS));
        var server = new GameServer(new ServerOptions(port: port, roomCount: 1, trainingRoomId: ROOM_ID));
        Task running = server.RunAsync(serverStop.Token);
        try
        {
            await WaitForServerAsync(port, deadline.Token);
            await VerifyRespawnAsync(port, deadline.Token);
            await VerifyCombatDrillAsync(port, deadline.Token);
            await VerifyTerrainRoutesAsync(port, deadline.Token);
        }
        finally
        {
            serverStop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task VerifyCombatDrillAsync(int port, CancellationToken token)
    {
        var (firstClient, firstAck) = await JoinAsync(port, "Drill Alpha", token);
        using var first = firstClient;
        var (secondClient, secondAck) = await JoinAsync(port, "Drill Bravo", token);
        using var second = secondClient;
        var (thirdClient, thirdAck) = await JoinAsync(port, "Drill Charlie", token);
        using var third = thirdClient;

        using var stop = new CancellationTokenSource();
        Task<uint>[] patrols =
        [
            StartPatrol(first, firstAck, CombatDrill.ROUTE_NUMBER, stop.Token, token),
            StartPatrol(second, secondAck, CombatDrill.ROUTE_NUMBER, stop.Token, token),
            StartPatrol(third, thirdAck, CombatDrill.ROUTE_NUMBER, stop.Token, token)
        ];
        Task ended = Task.WhenAny(patrols);
        try
        {
            var bots = new Dictionary<long, ConnectionTestClient>
            {
                [firstAck.PlayerId] = first,
                [secondAck.PlayerId] = second,
                [thirdAck.PlayerId] = third
            };
            KillEvent firstKill = default;
            await WaitForAsync(() => first.TryReadKillEvent(out firstKill), "Bots score an autonomous kill in the firing lane",
                token, ended);
            Require(bots.ContainsKey(firstKill.AttackerId) && bots.ContainsKey(firstKill.VictimId), "Both sides of the kill are drill bots");
            await WaitForAsync(() => first.LatestMatchState is MatchState state && state.ServerTick >= firstKill.ServerTick,
                "Bot kill reaches the authoritative roster", token, ended);
            MatchState score = first.LatestMatchState!;
            TeamId attackerTeam = score.Players.Single(player => player.PlayerId == firstKill.AttackerId).Team;
            TeamId victimTeam = score.Players.Single(player => player.PlayerId == firstKill.VictimId).Team;
            Require(attackerTeam != victimTeam && score.RedScore + score.BlueScore >= 1, "Opposing bots earn normal team score");

            ConnectionTestClient victim = bots[firstKill.VictimId];
            await WaitForAsync(() => victim.LatestMatchState?.Players.Any(player =>
                player.PlayerId == firstKill.VictimId && player.Health == MatchState.MAX_HEALTH && player.Deaths == 1) == true,
                "Killed drill bot respawns with its original identity", token, ended);
            long respawnTick = victim.LatestMatchState!.ServerTick;
            await WaitForAsync(() => victim.LatestSnapshot is WorldSnapshot snapshot && snapshot.ServerTick >= respawnTick &&
                Position(snapshot, firstKill.VictimId) is Vector3 foot && CombatDrill.Contains(foot),
                "Respawned drill bot returns to the firing lane", token, ended);
            await WaitForAsync(() =>
            {
                while (first.TryReadKillEvent(out KillEvent next))
                {
                    if (next.ServerTick > firstKill.ServerTick &&
                        (next.AttackerId == firstKill.VictimId || next.VictimId == firstKill.VictimId))
                    {
                        return true;
                    }
                }
                return false;
            }, "Respawned bot re-enters combat", token, ended);
            Console.WriteLine("PASS live Training patrol 4 bots fight autonomously, score, respawn, and resume combat");
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(patrols).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task VerifyRespawnAsync(int port, CancellationToken token)
    {
        var (botClient, botAck) = await JoinAsync(port, "Patrol center", token);
        using var bot = botClient;
        await WaitForAsync(() => bot.LatestMatchState?.Phase == MatchPhase.Playing, "A lone trainee can start", token);

        using var patrolStop = new CancellationTokenSource();
        Task<uint> patrol = StartPatrol(bot, botAck, CENTER_ROUTE, patrolStop.Token, token);
        try
        {
            await WaitForAsync(() => Position(bot, botAck.PlayerId) is Vector3 pos && IsCenterSouthPath(pos),
                "The central patrol reached the south side of the cover", token, patrol);

            var (shooterClient, shooterAck) = await JoinAsync(port, "Patrol observer", token);
            using var shooter = shooterClient;
            uint sequence = 0;
            float spawnZ = shooterAck.Z;
            sequence = await MoveToAsync(shooter, shooterAck.PlayerId, new Vector2(-8, spawnZ), sequence, token);
            sequence = await MoveToAsync(shooter, shooterAck.PlayerId, new Vector2(-8, -5.8f), sequence, token);
            await MoveToAsync(shooter, shooterAck.PlayerId, new Vector2(0, -5.8f), sequence, token);
            await KillOnSouthPathAsync(shooter, shooterAck.PlayerId, botAck.PlayerId, token);

            await WaitForAsync(() => bot.LatestMatchState?.Players.Any(player =>
                player.PlayerId == botAck.PlayerId && player.Health == 0 && player.Deaths == 1) == true,
                "The patrol saw its own death", token, patrol);
            await WaitForAsync(() => bot.LatestMatchState?.Players.Any(player =>
                player.PlayerId == botAck.PlayerId && player.Health == MatchState.MAX_HEALTH && player.Deaths == 1) == true,
                "The same player ID respawned", token, patrol);
            await WaitForAsync(() => Position(bot, botAck.PlayerId) is Vector3 pos && IsCenterSouthPath(pos),
                "The patrol returned to its assigned area after respawn", token, patrol);
            Console.WriteLine("PASS live Training patrol 1 reaches cover, dies, respawns and returns to the same route");
        }
        finally
        {
            patrolStop.Cancel();
            await patrol.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Require(await bot.EchoAsync("patrol stopped", token) == "patrol stopped", "A stopped patrol keeps its socket usable");
    }

    private static async Task VerifyTerrainRoutesAsync(int port, CancellationToken token)
    {
        var (eastClient, eastAck) = await JoinAsync(port, "Patrol bridge", token);
        using var east = eastClient;
        var (westClient, westAck) = await JoinAsync(port, "Patrol jumps", token);
        using var west = westClient;
        using var stop = new CancellationTokenSource();
        Task<uint> eastPatrol = StartPatrol(east, eastAck, BRIDGE_ROUTE, stop.Token, token);
        Task<uint> westPatrol = StartPatrol(west, westAck, JUMP_ROUTE, stop.Token, token);
        try
        {
            await Task.WhenAll(VerifyBridgeAsync(), VerifyJumpBlocksAsync());
            Console.WriteLine("PASS live Training patrol 2 crosses the bridge and patrol 3 lands on all five blocks");
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(eastPatrol, westPatrol).WaitAsync(TimeSpan.FromSeconds(5));
        }

        async Task VerifyBridgeAsync()
        {
            await WaitForAsync(() => Position(east, eastAck.PlayerId) is Vector3 pos &&
                MathF.Abs(pos.X - 4.5f) < 0.35f && pos.Z is > -1.9f and < 3.9f && MathF.Abs(pos.Y - 2.2f) < 0.12f,
                "The bridge patrol reached its elevated deck", token, eastPatrol);
            await WaitForAsync(() => Position(east, eastAck.PlayerId) is Vector3 pos &&
                MathF.Abs(pos.X - 4.5f) < 0.35f && (pos.Z > 8.2f || pos.Z < -7.9f) && MathF.Abs(pos.Y) < 0.12f,
                "The bridge patrol continued down to ground level", token, eastPatrol);
        }

        async Task VerifyJumpBlocksAsync()
        {
            (float Z, float Y)[] blocks = [(-11, 0.75f), (-6.9f, 1), (-1.9f, 1.35f), (4.1f, 1.15f), (10.75f, 1.4f)];
            var visited = new HashSet<int>();
            await WaitForAsync(() =>
            {
                if (Position(west, westAck.PlayerId) is Vector3 pos && MathF.Abs(pos.X + 10.4f) < 0.45f)
                {
                    for (int index = 0; index < blocks.Length; index++)
                    {
                        if (MathF.Abs(pos.Z - blocks[index].Z) < 0.65f && MathF.Abs(pos.Y - blocks[index].Y) < 0.13f)
                        {
                            visited.Add(index);
                        }
                    }
                }
                return visited.Count == blocks.Length;
            }, "The jump patrol visited all five block tops", token, westPatrol);
        }
    }

    private static Task<uint> StartPatrol(ConnectionTestClient client, JoinAck ack, int number,
        CancellationToken stopToken, CancellationToken connectionToken)
    {
        if (!PatrolMode.TryGetRoute(number, out PatrolRoute? route))
        {
            throw new InvalidOperationException($"Missing patrol route {number}.");
        }
        return PatrolMode.RunAsync(client, ack, route, 0, stopToken, connectionToken);
    }

    private static bool IsCenterSouthPath(Vector3 position) =>
        MathF.Abs(position.X) < 2.8f && position.Z is > -3.85f and < -3.1f && MathF.Abs(position.Y) < 0.12f;

    private static async Task KillOnSouthPathAsync(ConnectionTestClient shooter, long shooterId, long targetId, CancellationToken token)
    {
        Vector3? previous = null;
        long previousTick = 0;
        Vector3 lead = Vector3.Zero;
        while (!shooter.LatestMatchState!.Players.Any(player => player.PlayerId == targetId && player.Health == 0))
        {
            if (shooter.LatestSnapshot is WorldSnapshot snapshot && Position(snapshot, targetId) is Vector3 target &&
                Position(snapshot, shooterId) is Vector3 origin)
            {
                if (previous is Vector3 prior && snapshot.ServerTick > previousTick)
                {
                    float elapsedSeconds = (snapshot.ServerTick - previousTick) / (float)ServerOptions.DefaultTickRate;
                    lead = (target - prior) / elapsedSeconds * SHOT_LEAD_SECONDS;
                }
                if (snapshot.ServerTick > previousTick)
                {
                    previous = target;
                    previousTick = snapshot.ServerTick;
                }
                if (IsCenterSouthPath(target))
                {
                    Vector3 aim = Vector3.Normalize(target + lead - origin);
                    await shooter.SendRayAsync(new CameraRay(Vector3.UnitY, aim), token);
                    await Task.Delay(SHOT_INTERVAL_MS, token);
                    shooter.TryReadRaycastResult(out _);
                    continue;
                }
            }
            await Task.Delay(UPDATE_INTERVAL_MS, token);
        }
    }

    private static async Task<uint> MoveToAsync(ConnectionTestClient client, long playerId, Vector2 waypoint,
        uint sequence, CancellationToken token)
    {
        while (true)
        {
            if (Position(client, playerId) is Vector3 position)
            {
                Vector2 remaining = waypoint - new Vector2(position.X, position.Z);
                float distanceMeters = remaining.Length();
                if (distanceMeters <= WAYPOINT_TOLERANCE_METERS)
                {
                    await client.SendMoveAsync(new MoveInput(sequence++, 0, 0), token);
                    return sequence;
                }

                Vector2 movement = remaining / MathF.Max(distanceMeters, 0.75f);
                await client.SendMoveAsync(new MoveInput(sequence++, movement.X, movement.Y), token);
            }
            await Task.Delay(UPDATE_INTERVAL_MS, token);
        }
    }

    private static Vector3? Position(ConnectionTestClient client, long playerId) =>
        client.LatestSnapshot is WorldSnapshot snapshot ? Position(snapshot, playerId) : null;

    private static Vector3? Position(WorldSnapshot snapshot, long playerId)
    {
        foreach (SnapshotPlayer player in snapshot.Players)
        {
            if (player.PlayerId == playerId)
            {
                return new Vector3(player.X, player.Y, player.Z);
            }
        }
        return null;
    }

    private static async Task WaitForAsync(Func<bool> condition, string checkpoint, CancellationToken token, Task? patrol = null)
    {
        try
        {
            while (!condition())
            {
                if (patrol?.IsCompleted == true)
                {
                    await patrol;
                    throw new InvalidOperationException($"Patrol stopped before checkpoint: {checkpoint}.");
                }
                await Task.Delay(UPDATE_INTERVAL_MS, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new TimeoutException(checkpoint);
        }
    }

    private static async Task<(ConnectionTestClient Client, JoinAck Ack)> JoinAsync(int port, string name, CancellationToken token)
    {
        var client = new ConnectionTestClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", port, token);
            Require((await client.JoinMatchAsync(ROOM_ID, name, token)).Result == RoomJoinResult.Success, "Named training admission succeeds");
            return (client, await client.WaitForAdmissionAsync(token));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task WaitForServerAsync(int port, CancellationToken token)
    {
        while (true)
        {
            using var probe = new TcpClient();
            try
            {
                await probe.ConnectAsync(IPAddress.Loopback, port, token);
                return;
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused)
            {
                await Task.Delay(UPDATE_INTERVAL_MS, token);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
