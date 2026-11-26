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
            case MessageType.Jump:
                if (payload.Length != 0)
                {
                    throw new InvalidDataException("Jump expects an empty payload.");
                }
                if (Session is not null)
                {
                    _rooms.GetActiveMailbox(Session.SessionId)?.EnqueueAction(null);
                }
                return;
            case MessageType.CameraRay:
                CameraRay ray = ActionCodec.DecodeRay(payload);
                if (Session is not null)
                {
                    _rooms.GetActiveMailbox(Session.SessionId)?.EnqueueAction(ray);
                }
                return;
            case MessageType.AimedMoveInput:
                MoveInput aimedInput = PacketCodec.DecodeAimedMoveInput(payload, out AimAngles aim);
                if (Session is not null)
                {
                    _rooms.SubmitMovement(Session.SessionId, aimedInput, aim);
                }
                return;
            case MessageType.MoveInput:
                MoveInput input = PacketCodec.DecodeMoveInput(payload);
                if (Session is not null)
                {
                    _rooms.SubmitMovement(Session.SessionId, input);
                }
                return;
            case MessageType.EchoRequest:
                string message = PacketCodec.DecodeEcho(payload);
                Console.WriteLine($"Received [{context.Channel.RemoteAddress}]: {System.Text.Json.JsonSerializer.Serialize(message)}");
                frame = PacketCodec.EncodeEcho(MessageType.EchoResponse, message);
                break;
            case MessageType.JoinRoomRequest:
                JoinRoomRequest request = PacketCodec.DecodeJoinRoomRequest(payload);
                if (Session is null) throw new InvalidOperationException("Session is not active.");
                Session.ReplyToJoin(_rooms, request);
                return;
            default:
                throw new InvalidDataException("Server expects a request message.");
        }

        Session?.Send(frame);
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
