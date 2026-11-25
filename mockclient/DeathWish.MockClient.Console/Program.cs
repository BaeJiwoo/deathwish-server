using DeathWish.MockClient.Core;
using DeathWish.Protocol;
using System.Globalization;
using System.Numerics;

const int COMMAND_TIMEOUT_SECONDS = 5;

if (args.Length > 3 || !int.TryParse(args.ElementAtOrDefault(1) ?? "7777", out int port) || port is < 1 or > 65535)
{
    Console.Error.WriteLine("Usage: DeathWish.MockClient.Console [host] [port] [message]");
    return 1;
}

bool isOneShot = args.Length == 3;
if (isOneShot && args[2].Trim() == "/patrol")
{
    PrintPatrolRoutes();
    return 0;
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    using var client = new ConnectionTestClient();
    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
    {
        timeout.CancelAfter(TimeSpan.FromSeconds(COMMAND_TIMEOUT_SECONDS));
        await client.ConnectAsync(args.ElementAtOrDefault(0) ?? "127.0.0.1", port, timeout.Token);
    }
    Console.WriteLine("Connected. Enter a message, /join <roomId> [nickname], /patrol, /stop, /move <x> <z>, /shoot <x> <y> <z>, /match, /wasd, or /quit.");
    await using var patrol = new ConsolePatrol(client, cancellation.Token);
    JoinAck? admission = null;

    do
    {
        string? message;
        if (isOneShot)
        {
            message = args[2];
        }
        else
        {
            // Console input can block synchronously even through ReadLineAsync.
            Task<string?> input = Task.Run(() => Console.ReadLine());
            if (patrol.Running is Task running)
            {
                Task completed = await Task.WhenAny(input, client.Disconnected, running).WaitAsync(cancellation.Token);
                if (completed == running)
                {
                    await patrol.CompleteAsync();
                }
            }
            await Task.WhenAny(input, client.Disconnected).WaitAsync(cancellation.Token);
            if (client.Disconnected.IsCompleted)
            {
                Console.WriteLine($"Server connection closed. {client.DisconnectError?.Message}");
                return client.DisconnectError is null ? 0 : 1;
            }

            message = await input;
        }
        if (message is null || message.Trim() == "/quit")
        {
            break;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        string[] parts = message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.FirstOrDefault() == "/join")
        {
            string[] joinParts = message.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (joinParts.Length < 2 || !int.TryParse(joinParts[1], out int roomId) || roomId <= 0)
            {
                Console.Error.WriteLine("Usage: /join <positive roomId> [nickname]");
                if (isOneShot)
                {
                    return 1;
                }
                continue;
            }

            await patrol.StopAsync();
            timeout.CancelAfter(TimeSpan.FromSeconds(COMMAND_TIMEOUT_SECONDS));
            JoinAck? ack = await JoinAsync(client, roomId, joinParts.ElementAtOrDefault(2), timeout.Token);
            if (ack.HasValue)
            {
                admission = ack;
            }
            else if (isOneShot)
            {
                return 1;
            }
        }
        else if (parts.FirstOrDefault() == "/patrol")
        {
            string[] patrolParts = message.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
            if (patrolParts.Length == 1)
            {
                PrintPatrolRoutes();
                continue;
            }
            if (patrolParts.Length is not (2 or 4) || !int.TryParse(patrolParts[1], out int routeId))
            {
                PrintPatrolRoutes();
                if (isOneShot)
                {
                    return 1;
                }
                continue;
            }
            if (!PatrolMode.TryGetRoute(routeId, out PatrolRoute? route))
            {
                Console.Error.WriteLine($"Unknown patrol number {routeId}. Enter /patrol to list routes.");
                if (isOneShot)
                {
                    return 1;
                }
                continue;
            }
            if (patrolParts.Length == 4)
            {
                if (!int.TryParse(patrolParts[2], out int roomId) || roomId <= 0)
                {
                    Console.Error.WriteLine("Enter a positive room ID.");
                    if (isOneShot)
                    {
                        return 1;
                    }
                    continue;
                }
                await patrol.StopAsync();
                timeout.CancelAfter(TimeSpan.FromSeconds(COMMAND_TIMEOUT_SECONDS));
                JoinAck? ack = await JoinAsync(client, roomId, patrolParts[3], timeout.Token);
                if (!ack.HasValue)
                {
                    if (isOneShot)
                    {
                        return 1;
                    }
                    continue;
                }
                admission = ack;
            }
            if (admission is not JoinAck current || current.MapId != WorldMapId.TeamDeathmatch)
            {
                Console.Error.WriteLine("Join a team deathmatch or practice room with a nickname before starting patrol.");
                if (isOneShot)
                {
                    return 1;
                }
                continue;
            }

            await patrol.StartAsync(current, route);
            if (isOneShot)
            {
                await patrol.CompleteAsync();
                return client.DisconnectError is null ? 0 : 1;
            }
        }
        else if (parts.FirstOrDefault() == "/stop")
        {
            if (parts.Length != 1)
            {
                Console.Error.WriteLine("Usage: /stop");
                if (isOneShot)
                {
                    return 1;
                }
                continue;
            }
            if (patrol.Running is null)
            {
                Console.WriteLine("No patrol is active.");
                continue;
            }
            await patrol.StopAsync();
            Console.WriteLine("Patrol stopped.");
        }
        else if (parts.FirstOrDefault() == "/wasd")
        {
            if (admission is not JoinAck current)
            {
                Console.Error.WriteLine("Join a room before starting WASD mode.");
                continue;
            }
            await patrol.StopAsync();
            patrol.MovementSequence = await WasdMode.RunAsync(client, current.PlayerId, patrol.MovementSequence, cancellation.Token);
            Console.WriteLine("Enter /wasd, /move, /shoot, /patrol, /match, a message, or /quit.");
        }
        else if (parts.FirstOrDefault() == "/move")
        {
            if (parts.Length != 3 ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ||
                !float.IsFinite(x) || !float.IsFinite(z) || Math.Abs(x) > 1 || Math.Abs(z) > 1)
            {
                Console.Error.WriteLine("Usage: /move <x> <z>, each component within [-1, 1]. Use /move 0 0 to stop.");
                if (isOneShot)
                {
                    return 1;
                }
                continue;
            }
            if (!admission.HasValue)
            {
                Console.Error.WriteLine("Join a room before sending movement.");
                if (isOneShot)
                {
                    return 1;
                }
                continue;
            }

            await patrol.StopAsync();
            timeout.CancelAfter(TimeSpan.FromSeconds(COMMAND_TIMEOUT_SECONDS));
            uint sequence = patrol.MovementSequence;
            await client.SendMoveAsync(new MoveInput(sequence, x, z), timeout.Token);
            Console.WriteLine($"Movement sent: sequence={sequence}, X={x}, Z={z}. Expires without refresh; snapshots arrive asynchronously.");
            patrol.MovementSequence = unchecked(sequence + 1);
        }
        else if (parts.FirstOrDefault() == "/shoot")
        {
            if (!admission.HasValue)
            {
                Console.Error.WriteLine("Join a room before shooting.");
                if (isOneShot)
                {
                    return 1;
                }
                continue;
            }
            if (!TryGetAimDirection(parts, out Vector3 direction))
            {
                Console.Error.WriteLine("Usage: /shoot <x> <y> <z> with a finite, nonzero world-space direction.");
                if (isOneShot)
                {
                    return 1;
                }
                continue;
            }

            await patrol.StopAsync();
            timeout.CancelAfter(TimeSpan.FromSeconds(COMMAND_TIMEOUT_SECONDS));
            await client.SendRayAsync(new CameraRay(Vector3.UnitY, direction), timeout.Token);
            string? report;
            while (!client.TryReadRaycastResult(out report))
            {
                if (client.Disconnected.IsCompleted)
                {
                    throw new IOException("Server disconnected before responding to the shot.", client.DisconnectError);
                }
                await Task.Delay(10, timeout.Token);
            }
            Console.WriteLine(report);
        }
        else if (parts.FirstOrDefault() == "/match")
        {
            MatchState? state = client.LatestMatchState;
            if (state is null)
            {
                Console.WriteLine("No team deathmatch state received.");
                continue;
            }
            Console.WriteLine($"{state.Phase} | Red {state.RedScore} - {state.BlueScore} Blue");
            if (state.Winner is TeamId winner)
            {
                Console.WriteLine($"Winner: {winner}");
            }
            foreach (MatchPlayer player in state.Players)
            {
                string ping = player.PingMs < 0 ? "-" : $"{player.PingMs} ms";
                Console.WriteLine($"{player.Team} {player.Nickname} | HP {player.Health} | {player.Kills}/{player.Deaths}/{player.Assists} | {ping}");
            }
            while (client.TryReadKillEvent(out KillEvent kill))
            {
                Console.WriteLine($"{kill.AttackerNickname} > {kill.VictimNickname}");
            }
        }
        else
        {
            await patrol.StopAsync();
            timeout.CancelAfter(TimeSpan.FromSeconds(COMMAND_TIMEOUT_SECONDS));
            string response = await client.EchoAsync(message, timeout.Token);
            Console.WriteLine($"Sent: {message}");
            Console.WriteLine($"Received: {response}");
            Console.WriteLine("Echo PASS");
        }
    } while (!isOneShot);
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Connection test timed out after 5 seconds.");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Connection test failed: {exception.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
    Console.WriteLine("Disconnected.");
}

static async Task<JoinAck?> JoinAsync(ConnectionTestClient client, int roomId, string? nickname, CancellationToken cancellationToken)
{
    JoinRoomResponse response;
    try
    {
        response = nickname is null
            ? await client.JoinRoomAsync(roomId, cancellationToken)
            : await client.JoinMatchAsync(roomId, nickname, cancellationToken);
    }
    catch (Exception exception) when (!client.Disconnected.IsCompleted && exception is ArgumentException or InvalidDataException)
    {
        Console.Error.WriteLine(exception.Message);
        return null;
    }

    Console.WriteLine($"Join room {response.RoomId}: {response.Result}");
    if (response.Result != RoomJoinResult.Success)
    {
        return null;
    }

    JoinAck ack = await client.WaitForAdmissionAsync(cancellationToken);
    Console.WriteLine($"World ready: PlayerId={ack.PlayerId}, ServerTick={ack.ServerTick}, Map={ack.MapId}, Spawn=({ack.X}, {ack.Y}, {ack.Z})");
    return ack;
}

static void PrintPatrolRoutes()
{
    Console.WriteLine("/patrol <number> | /patrol <number> <roomId> <nickname> | /stop");
    foreach (PatrolRoute route in PatrolMode.Routes)
    {
        Console.WriteLine($"{route.Number} {route.Name}");
    }
}

static bool TryGetAimDirection(string[] parts, out Vector3 direction)
{
    direction = default;
    if (parts.Length != 4 ||
        !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
        !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
        !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
    {
        return false;
    }

    var aim = new Vector3(x, y, z);
    float length = aim.Length();
    if (!float.IsFinite(length) || length <= 0)
    {
        return false;
    }

    direction = aim / length;
    return true;
}

/// <summary>Serializes patrol start, stop and movement sequence handoff on the console command loop.</summary>
internal sealed class ConsolePatrol(ConnectionTestClient client, CancellationToken connectionToken) : IAsyncDisposable
{
    private CancellationTokenSource? _stop;
    private Task<uint>? _run;

    internal Task? Running => _run;
    internal uint MovementSequence { get; set; }

    internal async Task StartAsync(JoinAck admission, PatrolRoute route)
    {
        await StopAsync();
        _stop = new CancellationTokenSource();
        _run = PatrolMode.RunAsync(client, admission, route, MovementSequence, _stop.Token, connectionToken);
    }

    internal Task StopAsync() => FinishAsync(shouldStop: true);

    internal Task CompleteAsync() => FinishAsync(shouldStop: false);

    private async Task FinishAsync(bool shouldStop)
    {
        Task<uint>? run = _run;
        CancellationTokenSource? stop = _stop;
        _run = null;
        _stop = null;
        if (run is null || stop is null)
        {
            return;
        }

        // Stop the patrol loop without cancelling a packet write on the shared connection.
        using (stop)
        {
            if (shouldStop)
            {
                stop.Cancel();
            }
            MovementSequence = await run;
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
