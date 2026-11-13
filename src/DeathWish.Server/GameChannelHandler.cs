using DeathWish.Protocol;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;

namespace DeathWish.Server;

public sealed class GameChannelHandler : SimpleChannelInboundHandler<IByteBuffer>
{
    private readonly PacketReader _reader = new();
    private readonly SessionManager _sessions;
    private readonly RoomManager _rooms;

    public ClientSession? Session { get; private set; }

    public GameChannelHandler(SessionManager sessions, RoomManager rooms)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(rooms);
        _sessions = sessions;
        _rooms = rooms;
    }

    public override void ChannelActive(IChannelHandlerContext context)
    {
        if (Session is null)
        {
            Session = _sessions.Register(context.Channel);
            Console.WriteLine($"Connected [{Session.SessionId}]: {context.Channel.RemoteAddress}");
        }

        context.FireChannelActive();
    }

    protected override void ChannelRead0(IChannelHandlerContext context, IByteBuffer buffer)
    {
        byte[] received = new byte[buffer.ReadableBytes];
        buffer.ReadBytes(received);
        _reader.Feed(received, (type, payload) => HandlePacket(context, type, payload));
    }

    private void HandlePacket(IChannelHandlerContext context, MessageType type, byte[] payload)
    {
        byte[] frame;
        switch (type)
        {
            case MessageType.EchoRequest:
                string message = PacketCodec.DecodeEcho(payload);
                Console.WriteLine($"Received [{context.Channel.RemoteAddress}]: {System.Text.Json.JsonSerializer.Serialize(message)}");
                frame = PacketCodec.EncodeEcho(MessageType.EchoResponse, message);
                break;
            case MessageType.JoinRoomRequest:
                JoinRoomRequest request = PacketCodec.DecodeJoinRoomRequest(payload);
                RoomJoinResult result = Session is null
                    ? RoomJoinResult.SessionUnavailable
                    : _rooms.Join(Session.SessionId, request.RoomId);
                frame = PacketCodec.EncodeJoinRoomResponse(new JoinRoomResponse(request.RoomId, result));
                break;
            default:
                throw new InvalidDataException("Server expects a request message.");
        }

        _ = SendResponseAsync(context, Unpooled.WrappedBuffer(frame));
    }

    private static async Task SendResponseAsync(IChannelHandlerContext context, IByteBuffer response)
    {
        // Ownership of the response buffer transfers to the outbound pipeline.
        try
        {
            await context.WriteAndFlushAsync(response);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Response failed: {exception.Message}");
            await context.CloseAsync();
        }
    }

    public override void ChannelInactive(IChannelHandlerContext context)
    {
        if (Session?.TryMarkDisconnected() == true)
        {
            _rooms.Leave(Session.SessionId);
            _sessions.Remove(Session.SessionId);
            Console.WriteLine($"Disconnected [{Session.SessionId}]: {context.Channel.RemoteAddress}");
        }

        context.FireChannelInactive();
    }

    public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
    {
        Console.Error.WriteLine($"Connection error [{context.Channel.RemoteAddress}]: {exception.Message}");
        _ = context.CloseAsync();
    }
}
