using DeathWish.Protocol;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;

namespace DeathWish.Server;

public sealed class GameChannelHandler : SimpleChannelInboundHandler<IByteBuffer>
{
    private readonly PacketReader _reader = new();

    public override void ChannelActive(IChannelHandlerContext context)
    {
        Console.WriteLine($"Connected: {context.Channel.RemoteAddress}");
        context.FireChannelActive();
    }

    protected override void ChannelRead0(IChannelHandlerContext context, IByteBuffer buffer)
    {
        byte[] received = new byte[buffer.ReadableBytes];
        buffer.ReadBytes(received);
        _reader.Feed(received, (type, payload) => HandlePacket(context, type, payload));
    }

    private static void HandlePacket(IChannelHandlerContext context, MessageType type, byte[] payload)
    {
        if (type != MessageType.EchoRequest)
            throw new InvalidDataException("Server expects EchoRequest.");

        string message = PacketCodec.DecodeEcho(payload);
        Console.WriteLine($"Received [{context.Channel.RemoteAddress}]: {System.Text.Json.JsonSerializer.Serialize(message)}");
        byte[] frame = PacketCodec.EncodeEcho(MessageType.EchoResponse, message);
        _ = SendEchoAsync(context, Unpooled.WrappedBuffer(frame));
    }
    private static async Task SendEchoAsync(IChannelHandlerContext context, IByteBuffer response)
    {
        // Ownership of the response buffer transfers to the outbound pipeline.
        try
        {
            await context.WriteAndFlushAsync(response);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Echo failed: {exception.Message}");
            await context.CloseAsync();
        }
    }

    public override void ChannelInactive(IChannelHandlerContext context)
    {
        Console.WriteLine($"Disconnected: {context.Channel.RemoteAddress}");
        context.FireChannelInactive();
    }

    public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
    {
        Console.Error.WriteLine($"Connection error [{context.Channel.RemoteAddress}]: {exception.Message}");
        _ = context.CloseAsync();
    }
}
