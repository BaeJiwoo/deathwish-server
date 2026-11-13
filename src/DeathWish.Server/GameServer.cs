using System.Net;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Groups;
using DotNetty.Transport.Channels.Sockets;

namespace DeathWish.Server;

public sealed class GameServer
{
    private readonly IPEndPoint _endpoint;

    public GameServer(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var acceptGroup = new MultithreadEventLoopGroup(1);
        MultithreadEventLoopGroup? workerGroup = null;
        IChannel? listener = null;
        var connections = new DefaultChannelGroup(acceptGroup.GetNext());
        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions);

        try
        {
            workerGroup = new MultithreadEventLoopGroup();

            var bootstrap = new ServerBootstrap()
                .Group(acceptGroup, workerGroup)
                .Channel<TcpServerSocketChannel>()
                .ChildHandler(new ActionChannelInitializer<IChannel>(channel =>
                {
                    connections.Add(channel);
                    channel.Pipeline.AddLast(new GameChannelHandler(sessions, rooms));
                }));

            listener = await bootstrap.BindAsync(_endpoint);
            Console.WriteLine($"Listening on {listener.LocalAddress}. Press Ctrl+C to stop.");

            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                if (listener is not null)
                {
                    await listener.CloseAsync();
                }
            }
            finally
            {
                try
                {
                    await connections.CloseAsync();
                }
                finally
                {
                    var shutdownTasks = new List<Task>
                    {
                        acceptGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(5))
                    };

                    if (workerGroup is not null)
                    {
                        shutdownTasks.Add(workerGroup.ShutdownGracefullyAsync(
                            TimeSpan.Zero, TimeSpan.FromSeconds(5)));
                    }

                    await Task.WhenAll(shutdownTasks);
                }
            }
        }
    }
}
