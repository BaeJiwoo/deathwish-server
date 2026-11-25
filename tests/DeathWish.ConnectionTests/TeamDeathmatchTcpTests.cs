using System.Net;
using System.Net.Sockets;
using System.Numerics;
using DeathWish.MockClient.Core;
using DeathWish.Protocol;
using DeathWish.Server;

internal static class TeamDeathmatchTcpTests
{
    private const int ROOM_ID = 1;
    private const int TICK_RATE = 10;
    private static readonly TimeSpan TickDuration = TimeSpan.FromSeconds(1d / TICK_RATE);

    internal static async Task RunAsync()
    {
        int port;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }

        var clock = new ManualTimeProvider();
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new GameServer(new ServerOptions(port: port, roomCount: 1, tickRate: TICK_RATE,
            teamDeathmatchRoomId: ROOM_ID), clock);
        Task running = server.RunAsync(lifetime.Token);
        var clients = new List<ConnectionTestClient>();
        try
        {
            await clock.NextDelayAsync();
            for (int index = 0; index < MatchState.MAX_PLAYERS - 1; index++)
            {
                ConnectionTestClient client = await ConnectAsync();
                RoomJoinResult result = (await client.JoinMatchAsync(ROOM_ID, $"플레이어 {index + 1}", deadline.Token)).Result;
                Require(result == RoomJoinResult.Success, $"TCP participant {index + 1} accepted");
            }

            await AdvanceAsync();
            ConnectionTestClient observer = clients[0];
            ConnectionTestClient target = clients[1];
            JoinAck attackerAck = await observer.WaitForAdmissionAsync(deadline.Token);
            JoinAck victimAck = await target.WaitForAdmissionAsync(deadline.Token);
            MatchState waiting = await WaitForStateAsync(observer, state => state.Players.Count == MatchState.MAX_PLAYERS - 1, deadline.Token);
            Require(waiting.Phase == MatchPhase.Waiting && attackerAck.MapId == WorldMapId.TeamDeathmatch, "Nine live sessions are waiting in the arena");

            ConnectionTestClient last = await ConnectAsync();
            Require((await last.JoinMatchAsync(ROOM_ID, "마지막", deadline.Token)).Result == RoomJoinResult.Success,
                "Tenth TCP participant accepted");
            await AdvanceAsync();
            await last.WaitForAdmissionAsync(deadline.Token);
            MatchState playing = await WaitForStateAsync(observer, state => state.Phase == MatchPhase.Playing, deadline.Token);
            Require(playing.Players.Count(player => player.Team == TeamId.Red) == MatchState.MAX_TEAM_SIZE &&
                playing.Players.Count(player => player.Team == TeamId.Blue) == MatchState.MAX_TEAM_SIZE, "Default match is full 5v5");

            ConnectionTestClient extra = await ConnectAsync();
            Require((await extra.JoinMatchAsync(ROOM_ID, "초과", deadline.Token)).Result == RoomJoinResult.WorldUnavailable,
                "An eleventh live session is refused");
            while (observer.LatestMatchState!.Players.Any(player => player.PingMs < 0))
            {
                await Task.Delay(10, deadline.Token);
                await AdvanceAsync();
            }
            await WaitForStateAsync(target, state => state.Players.Any(player => player.PlayerId == attackerAck.PlayerId &&
                player.PingMs >= 0), deadline.Token);

            await observer.SendRayAsync(new CameraRay(Vector3.UnitY, Vector3.UnitZ), deadline.Token);
            await observer.EchoAsync("Shot queued", deadline.Token);
            await AdvanceAsync();
            await WaitForStateAsync(observer, state => HealthFor(state, victimAck.PlayerId) == MatchState.MAX_HEALTH - 25, deadline.Token);
            await WaitForStateAsync(target, state => HealthFor(state, victimAck.PlayerId) == MatchState.MAX_HEALTH - 25, deadline.Token);

            target.Dispose();
            while (observer.LatestMatchState!.Players.Any(player => player.PlayerId == victimAck.PlayerId))
            {
                await Task.Delay(10, deadline.Token);
                await AdvanceAsync();
            }
            Require(observer.LatestMatchState!.Phase == MatchPhase.Playing, "Mid-round departure removes the player");
            Require((await extra.JoinMatchAsync(ROOM_ID, "교체", deadline.Token)).Result == RoomJoinResult.Success,
                "A new session can fill the vacated team slot");
            await AdvanceAsync();
            JoinAck replacement = await extra.WaitForAdmissionAsync(deadline.Token);
            MatchState restored = await WaitForStateAsync(observer, state => state.Players.Count == MatchState.MAX_PLAYERS, deadline.Token);
            Require(restored.Players.Single(player => player.PlayerId == replacement.PlayerId).Team == TeamId.Blue,
                "Replacement fills the smaller team");
            Console.WriteLine("PASS live TeamDeathmatch TCP ten admissions, phase, RTT, damage, overflow, departure and replacement");
        }
        finally
        {
            foreach (ConnectionTestClient client in clients)
            {
                client.Dispose();
            }
            lifetime.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }

        async Task<ConnectionTestClient> ConnectAsync()
        {
            var client = new ConnectionTestClient();
            clients.Add(client);
            await client.ConnectAsync("127.0.0.1", port, deadline.Token);
            return client;
        }

        async Task AdvanceAsync()
        {
            clock.Advance(TickDuration);
            await clock.NextDelayAsync();
        }
    }

    private static int? HealthFor(MatchState state, long playerId)
    {
        foreach (MatchPlayer player in state.Players)
        {
            if (player.PlayerId == playerId)
            {
                return player.Health;
            }
        }
        return null;
    }

    private static async Task<MatchState> WaitForStateAsync(ConnectionTestClient client, Func<MatchState, bool> matches, CancellationToken token)
    {
        while (true)
        {
            MatchState? state = client.LatestMatchState;
            if (state != null && matches(state))
            {
                return state;
            }
            if (client.Disconnected.IsCompleted)
            {
                throw new IOException("Client disconnected before receiving the expected match state.", client.DisconnectError);
            }
            await Task.Delay(5, token);
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
