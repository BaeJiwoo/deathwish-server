using System.Numerics;
using System.Net;
using System.Net.Sockets;
using DeathWish.MockClient.Core;
using DeathWish.Protocol;
using DeathWish.Server;

static class GameServerTests
{
    public static async Task RunAsync()
    {
        await VerifyShutdown(false);
        await VerifyShutdown(true);
        await VerifyBindFailure();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await new GameServer(new ServerOptions()).RunAsync(cancelled.Token);
            throw new Exception("Pre-cancelled server unexpectedly started");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        Console.WriteLine("PASS GameServer TCP admission, cancellation, loop failure, port reuse, and bind failure");
    }

    private static async Task VerifyShutdown(bool injectFailure)
    {
        int port;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }

        var clock = new ManualTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = new GameServer(new ServerOptions(port: port, tickRate: 10), clock);
        Task running = server.RunAsync(cancellation.Token);
        try
        {
            // The first loop wait proves that binding has completed.
            await clock.NextDelayAsync();
            using var first = new ConnectionTestClient();
            using var second = new ConnectionTestClient();
            await first.ConnectAsync("127.0.0.1", port, deadline.Token);
            await second.ConnectAsync("127.0.0.1", port, deadline.Token);
            Require((await first.JoinRoomAsync(1, deadline.Token)).Result == RoomJoinResult.Success, "First live room join");
            Require((await second.JoinRoomAsync(1, deadline.Token)).Result == RoomJoinResult.Success, "Second live room join");
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await clock.NextDelayAsync();
            JoinAck firstAck = await first.WaitForAdmissionAsync(deadline.Token);
            JoinAck secondAck = await second.WaitForAdmissionAsync(deadline.Token);
            Require(firstAck.PlayerId != secondAck.PlayerId && firstAck.ServerTick == 1 && secondAck.ServerTick == 1,
                "Production pipeline delivers world admission");
            Require(await first.EchoAsync("world running", deadline.Token) == "world running", "Networking alongside world loop");

            if (injectFailure)
            {
                var failure = new InvalidOperationException("Injected server loop failure");
                clock.OnTimestamp = () =>
                {
                    // Cancellation must not conceal a simultaneous loop failure.
                    cancellation.Cancel();
                    throw failure;
                };
                clock.Advance(TimeSpan.FromMilliseconds(100));
                try { await running.WaitAsync(deadline.Token); throw new Exception("Expected server loop failure"); }
                catch (InvalidOperationException exception) when (ReferenceEquals(exception, failure)) { }
            }
            else
            {
                Vector3 toOther = new(secondAck.X - firstAck.X, 0, secondAck.Z - firstAck.Z);
                var ray = new CameraRay(Vector3.UnitY, Vector3.Normalize(toOther));
                await first.SendRayAsync(ray, deadline.Token);
                await first.SendJumpAsync(deadline.Token);
                // The echo acknowledges parsing both prior frames on this TCP connection.
                await first.EchoAsync("actions queued", deadline.Token);
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await clock.NextDelayAsync();
                string? report;
                while (!first.TryReadRaycastResult(out report))
                {
                    await Task.Delay(1, deadline.Token);
                }
                Require(report!.Contains($"hit player {secondAck.PlayerId} "), "Closest other player in camera ray");
                Require(!second.TryReadRaycastResult(out _), "Raycast result is private to requester");
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await clock.NextDelayAsync();
                while ((second.LatestSnapshot?.ServerTick ?? 0) < 3)
                {
                    await Task.Delay(1, deadline.Token);
                }
                Require(second.LatestSnapshot!.Players.Single(p => p.PlayerId == firstAck.PlayerId).Y > 0,
                    "Observer receives jump through the normal snapshot");
                Console.WriteLine("PASS TCP camera ray hits second player, private reply and replicated jump");
                cancellation.Cancel();
                await running.WaitAsync(deadline.Token);
            }

            await Task.WhenAll(first.Disconnected, second.Disconnected).WaitAsync(deadline.Token);
            using var reused = new TcpListener(IPAddress.Loopback, port);
            reused.Start();
        }
        finally
        {
            cancellation.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch when (running.IsCompleted) { }
        }
    }

    private static async Task VerifyBindFailure()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        bool loopStarted = false;
        var clock = new ManualTimeProvider { OnTimestamp = () => loopStarted = true };
        Task running = new GameServer(new ServerOptions(port: port), clock).RunAsync(default);
        try { await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch when (running.IsFaulted) { }
        Require(running.IsFaulted && !loopStarted, "Bind failure propagates without starting world loop");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
