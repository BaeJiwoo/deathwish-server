using System.Net;
using DeathWish.Server;

if (args.Length > 2 ||
    !IPAddress.TryParse(args.ElementAtOrDefault(0) ?? "127.0.0.1", out var address) ||
    !int.TryParse(args.ElementAtOrDefault(1) ?? "7777", out var port) ||
    port is < 1 or > 65535)
{
    Console.Error.WriteLine("Usage: DeathWish.Server [IP address] [port 1-65535]");
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
    var server = new GameServer(new IPEndPoint(address, port));
    await server.RunAsync(cancellation.Token);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
