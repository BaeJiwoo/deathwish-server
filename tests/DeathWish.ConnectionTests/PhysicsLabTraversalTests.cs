using System.Numerics;
using DeathWish.Protocol;
using DeathWish.Server;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;

internal static class PhysicsLabTraversalTests
{
    private const float TICK_SECONDS = 1f / 30;
    private const float WALL_CENTER_LIMIT_METERS = 25f - 0.65f / 2;
    private const float WALL_TOLERANCE_METERS = 0.02f;
    private const float RAMP_SLOPE = 6f / 29;

    internal static void Run()
    {
        using var player = new TestPlayer();
        // Approach the ramp from its low end without crossing the ring or a vertical side.
        player.Walk(0, 1, 120);
        player.Walk(-1, 0, 72);
        Require(Vector3.Distance(player.Position, new Vector3(-12, 0, 20)) < 0.015f, "Ramp approach", player.Position);

        TraverseRamp(player, -1, 204);
        Vector3 platform = player.Position;
        Require(platform.Z < -13 && MathF.Abs(platform.Y - 6) < 0.015f, "On the platform past the seam", platform);
        player.Walk(0, 0, 30);
        Require(Vector3.Distance(player.Position, platform) < 0.015f, "Stationary on platform", player.Position);

        TraverseRamp(player, 1, 204);
        Vector3 bottom = player.Position;
        Require(bottom.Z > 19 && MathF.Abs(bottom.Y) < 0.015f, "Back on the floor", bottom);
        Console.WriteLine($"PASS PhysicsLab ramp round trip: platform {platform}, return {bottom}");

        player.Walk(0, 1, 45);
        Vector3 firstContact = player.Position;
        for (int tick = 0; tick < 60; tick++)
        {
            Vector3 position = player.Move(0, 1);
            Require(Vector3.Distance(position, firstContact) < WALL_TOLERANCE_METERS &&
                MathF.Abs(position.Z - WALL_CENTER_LIMIT_METERS) < WALL_TOLERANCE_METERS, "Head-on wall stop", position);
        }

        player.Walk(0, -1, 12);
        Vector3 diagonalStart = player.Position;
        player.Walk(1, 1, 20);
        Vector3 diagonalContact = player.Position;
        Require(diagonalContact.Z - diagonalStart.Z > 1.9f &&
            MathF.Abs(diagonalContact.Z - WALL_CENTER_LIMIT_METERS) < WALL_TOLERANCE_METERS &&
            diagonalContact.X - diagonalStart.X > 2.2f, "Approach wall diagonally from the floor", diagonalContact);

        for (int tick = 0; tick < 60; tick++)
        {
            Vector3 previous = player.Position;
            Vector3 position = player.Move(1, 1);
            Require(MathF.Abs(position.Z - WALL_CENTER_LIMIT_METERS) < WALL_TOLERANCE_METERS &&
                MathF.Abs(position.Y) < WALL_TOLERANCE_METERS && position.X > previous.X + 0.10f,
                "Diagonal wall slide", position);
        }
        Vector3 slideEnd = player.Position;
        float expectedSlideMeters = GameWorld.MOVEMENT_SPEED_METERS_PER_SECOND * (60 * TICK_SECONDS) / MathF.Sqrt(2);
        Require(MathF.Abs(slideEnd.X - diagonalContact.X - expectedSlideMeters) < WALL_TOLERANCE_METERS,
            "Normalized tangential movement", slideEnd);
        player.Walk(0, -1, 10);
        Require(player.Position.Z < slideEnd.Z - 1.5f, "Can walk away from wall", player.Position);
        Console.WriteLine($"PASS PhysicsLab wall: head-on {firstContact}, diagonal {diagonalContact} to {slideEnd}");

        VerifyPlatformOpening();
    }

    private static void VerifyPlatformOpening()
    {
        using var player = new TestPlayer();
        player.Walk(0, 1, 120);
        player.Walk(-1, 0, 72);
        player.Walk(0, -1, 204);
        player.Walk(-1, 0, 30);
        Require(MathF.Abs(player.Position.Y - 6) < 0.015f, "Walk across the platform before the opening", player.Position);
        player.Walk(0, -1, 26);
        Vector3 inOpening = player.Position;
        Require(inOpening.Y > 0 && inOpening.Y < 6 && inOpening.Z < -16, "Fall into the opening while walking", inOpening);
        player.Walk(0, 0, 90);
        Vector3 landed = player.Position;
        Require(MathF.Abs(landed.Y) < 0.015f && MathF.Abs(landed.X - inOpening.X) < 0.01f &&
            MathF.Abs(landed.Z - inOpening.Z) < 0.01f, "Land below the opening after releasing input", landed);
        Console.WriteLine($"PASS PhysicsLab platform opening: airborne {inOpening}, landed {landed}");
    }

    private static void TraverseRamp(TestPlayer player, int directionZ, int ticks)
    {
        for (int tick = 0; tick < ticks; tick++)
        {
            Vector3 previous = player.Position;
            Vector3 next = player.Move(0, directionZ);
            // The uphill edge of the upright box supports the body before its center crosses the seam.
            float expectedY = Math.Clamp((18 - next.Z + 0.325f) * RAMP_SLOPE, 0, 6);
            Require((next.Z - previous.Z) * directionZ > 0.12f && MathF.Abs(next.X + 12) < 0.02f,
                "Continuous forward progress on ramp", next);
            Require(MathF.Abs(next.Y - expectedY) < 0.10f && MathF.Abs(next.Y - previous.Y) < 0.085f,
                $"Continuous ramp height, expected {expectedY:F3} m", next);
        }
    }

    private static void Require(bool condition, string message, Vector3 position)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{message}: {position}");
        }
    }

    private sealed class TestPlayer : IDisposable
    {
        private readonly ManualTimeProvider _clock = new();
        private readonly EmbeddedChannel _channel;
        private readonly GameWorld _world;
        private uint _sequence;

        internal Vector3 Position => _world.GetPlayers().Single().Position;

        internal TestPlayer()
        {
            var sessions = new SessionManager();
            var rooms = new RoomManager(sessions, 1, 4, 4, _clock);
            _world = rooms.GetWorlds().Single();
            _channel = new EmbeddedChannel(new GameChannelHandler(sessions, rooms));
            _channel.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeJoinRoomRequest(new JoinRoomRequest(1))));
            _world.Tick(TICK_SECONDS);
        }

        internal Vector3 Move(float x, float z)
        {
            _clock.Advance(TimeSpan.FromSeconds(TICK_SECONDS));
            var input = new MoveInput(_sequence++, x, z);
            _channel.WriteInbound(Unpooled.WrappedBuffer(PacketCodec.EncodeMoveInput(input)));
            _world.Tick(TICK_SECONDS);
            return Position;
        }

        internal void Walk(float x, float z, int ticks)
        {
            for (int tick = 0; tick < ticks; tick++)
            {
                Move(x, z);
            }
        }

        public void Dispose()
        {
            _channel.FinishAndReleaseAll();
            _world.Shutdown();
        }
    }
}
