using DeathWish.Server;

ServerOptions options;
try
{
    options = ServerOptionsLoader.Load(args);
}
catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Server configuration error: {exception.Message}");
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
    var server = new GameServer(options);
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
