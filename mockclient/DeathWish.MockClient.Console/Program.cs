using DeathWish.MockClient.Core;
using DeathWish.Protocol;

if (args.Length > 3 || !int.TryParse(args.ElementAtOrDefault(1) ?? "7777", out int port) || port is < 1 or > 65535)
{
    Console.Error.WriteLine("Usage: DeathWish.MockClient.Console [host] [port] [message]");
    return 1;
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
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(args.ElementAtOrDefault(0) ?? "127.0.0.1", port, timeout.Token);
    }
    Console.WriteLine("Connected. Enter a message, /join <roomId>, or /quit to disconnect.");

    do
    {
        string? message;
        if (args.Length == 3)
        {
            message = args[2];
        }
        else
        {
            // Console input can block synchronously even through ReadLineAsync.
            Task<string?> input = Task.Run(() => Console.ReadLine());
            await Task.WhenAny(input, client.Disconnected).WaitAsync(cancellation.Token);
            if (client.Disconnected.IsCompleted)
            {
                Console.WriteLine($"Server connection closed. {client.DisconnectError?.Message}");
                return client.DisconnectError is null ? 0 : 1;
            }

            message = await input;
        }
        if (message is null or "/quit") break;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        string[] parts = message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.FirstOrDefault() == "/join")
        {
            if (parts.Length != 2 || !int.TryParse(parts[1], out int roomId) || roomId <= 0)
            {
                Console.Error.WriteLine("Usage: /join <positive roomId>");
                if (args.Length == 3) return 1;
                continue;
            }

            JoinRoomResponse response = await client.JoinRoomAsync(roomId, timeout.Token);
            Console.WriteLine($"Join room {response.RoomId}: {response.Result}");
            if (args.Length == 3 && response.Result != RoomJoinResult.Success) return 1;
        }
        else
        {
            string response = await client.EchoAsync(message, timeout.Token);
            Console.WriteLine($"Sent: {message}");
            Console.WriteLine($"Received: {response}");
            Console.WriteLine("Echo PASS");
        }
    } while (args.Length != 3);
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
