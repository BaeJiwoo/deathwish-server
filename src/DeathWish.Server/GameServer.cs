using System.Net;
using DotNetty.Codecs;
using DeathWish.Protocol;
using System.Runtime.ExceptionServices;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Groups;
using DotNetty.Transport.Channels.Sockets;

namespace DeathWish.Server;

public sealed class GameServer
{
    private readonly IPEndPoint _endpoint;
    private readonly int _roomCount;
    private readonly int _tickRate;
    private readonly TimeProvider _timeProvider;

    public GameServer(ServerOptions options) : this(options, TimeProvider.System) { }

    internal GameServer(ServerOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _endpoint = new IPEndPoint(IPAddress.Parse(options.Address), options.Port);
        _roomCount = options.RoomCount;
        _tickRate = options.TickRate;
        _timeProvider = timeProvider;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sessions = new SessionManager();
        var rooms = new RoomManager(sessions, _roomCount, GameWorld.DEFAULT_ADMISSION_CAPACITY,
            GameWorld.DEFAULT_MAX_COMMANDS_PER_TICK, _timeProvider);
        var worldLoop = new WorldLoop(rooms.GetWorlds(), _tickRate, _timeProvider);
        using var loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? worldTask = null;
        var failures = new List<Exception>();
        var acceptGroup = new MultithreadEventLoopGroup(1);
        MultithreadEventLoopGroup? workerGroup = null;
        IChannel? listener = null;
        var connections = new DefaultChannelGroup(acceptGroup.GetNext());

        try
        {
            workerGroup = new MultithreadEventLoopGroup();

            var bootstrap = new ServerBootstrap()
                .Group(acceptGroup, workerGroup)
                .Channel<TcpServerSocketChannel>()
                .ChildHandler(new ActionChannelInitializer<IChannel>(channel =>
                {
                    connections.Add(channel);
                    channel.Pipeline.AddLast(new LengthFieldBasedFrameDecoder(PacketCodec.HEADER_SIZE + PacketCodec.MAX_PAYLOAD_LENGTH, 0, 2, 1, 0));
                    channel.Pipeline.AddLast(new GameChannelHandler(sessions, rooms));
                }));

            listener = await bootstrap.BindAsync(_endpoint);
            Console.WriteLine($"Listening on {listener.LocalAddress}. Rooms: {_roomCount}. Tick rate: {_tickRate} Hz. Press Ctrl+C to stop.");

            // The loop owns simulation work independently of DotNetty's event loops.
            worldTask = Task.Run(() => worldLoop.RunAsync(loopCancellation.Token));
            await Task.WhenAny(worldTask, Task.Delay(Timeout.Infinite, loopCancellation.Token));
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            // Observe the loop once, and attempt every cleanup even when an earlier operation fails.
            rooms.StopAdmission();
            await CleanupAsync(() => loopCancellation.CancelAsync());
            if (listener is not null) await CleanupAsync(() => listener.CloseAsync());
            if (worldTask is not null) await CleanupAsync(() => worldTask);
            else await CleanupAsync(() => worldLoop.RunAsync(new CancellationToken(true)));
            await CleanupAsync(() => connections.CloseAsync());
            await Task.WhenAll(
                CleanupAsync(() => acceptGroup.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(5))),
                CleanupAsync(() => workerGroup?.ShutdownGracefullyAsync(TimeSpan.Zero, TimeSpan.FromSeconds(5)) ?? Task.CompletedTask));
        }

        if (worldTask is not null)
        {
            WorldLoopStatistics statistics = worldLoop.GetStatistics();
            try
            {
                Console.WriteLine($"World loop stopped. Ticks: {statistics.CompletedTickCount}, overruns: {statistics.OverrunCount}, " +
                    $"last: {statistics.LastTickDuration.TotalMilliseconds:F3} ms, max: {statistics.MaxTickDuration.TotalMilliseconds:F3} ms, " +
                    $"dropped: {statistics.DroppedTime.TotalMilliseconds:F3} ms.");
            }
            catch (Exception exception) { failures.Add(exception); }
        }

        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Server execution or cleanup failed.", failures);

        async Task CleanupAsync(Func<Task> cleanup)
        {
            try { await cleanup(); }
            catch (Exception exception)
            {
                lock (failures) failures.Add(exception);
            }
        }
    }
}
