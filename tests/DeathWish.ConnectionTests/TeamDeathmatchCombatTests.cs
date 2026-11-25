using System.Numerics;
using DeathWish.Protocol;
using DeathWish.Server;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;

internal static class TeamDeathmatchCombatTests
{
    private const int TICK_RATE = 30;
    private const float TICK_SECONDS = 1f / TICK_RATE;
    private const int SHOT_INTERVAL_TICKS = 8;
    private const int RESPAWN_CHECK_TICKS = 3 * TICK_RATE + 2;
    private const int ARENA_ROOM_ID = 1;
    private const int SHOT_DAMAGE = 25;
    private const int WINNING_SCORE = 10;

    internal static void Run()
    {
        VerifyCombat();
        VerifyCover();
        VerifyLatency();
        VerifyTraining();
        Console.WriteLine("PASS TeamDeathmatch names, teams, waiting, cover, cooldown, health, kills, assists, respawn, winner, reset and RTT");
    }

    private static void VerifyCombat()
    {
        using var match = new Arena(teamSize: 2);
        Peer red = match.Join("빨강", receiveArenaShots: true);
        Peer blue = match.Join("파랑", receiveArenaShots: true);
        Peer ally = match.Join("동료");
        match.Advance();

        Require(red.State!.Phase == MatchPhase.Waiting, "Combat room waits until both teams are full");
        red.Shoot(Vector3.UnitZ);
        match.Advance();
        Require(blue.Player(blue).Health == MatchState.MAX_HEALTH, "Waiting disables damage");
        Require(red.Rays.Single() == "Raycast miss" && red.Shots.Count == 0, "Waiting returns the original reply without broadcasting a shot");

        Peer blueAlly = match.Join("파랑 동료");
        match.Advance();
        Require(red.State!.Phase == MatchPhase.Playing && blueAlly.State!.Phase == MatchPhase.Playing, "Both teams can play when full");
        Require(red.Player(red).Nickname == "빨강" && blue.Player(red).Nickname == "빨강", "Names replicate to both teams");
        Require(red.Player(red).Team == red.Player(ally).Team && red.Player(red).Team != red.Player(blue).Team,
            "Teams balance as players join");
        Require(red.Ack!.Value.Z == -12f && blue.Ack!.Value.Z == 12f, "Opposing players spawn at opposite ends");

        red.Shoot(Vector3.UnitX);
        match.Advance(SHOT_INTERVAL_TICKS);
        Require(ally.Player(ally).Health == MatchState.MAX_HEALTH, "Friendly fire cannot damage teammates");
        Require(red.Shots.Single().Outcome == ArenaShotOutcome.PlayerContact && blue.Shots.Single().Outcome == ArenaShotOutcome.PlayerContact,
            "The shooter and an opted-in observer see a friendly contact without damage");
        Require(ally.Shots.Count == 0 && blueAlly.Shots.Count == 0, "Existing clients receive no new frame type");

        red.Shoot(Vector3.UnitZ);
        match.Advance();
        Require(blue.Player(blue).Health == MatchState.MAX_HEALTH - SHOT_DAMAGE, "Server applies the first hit");
        ArenaShot firstDamage = red.Shots[^1];
        ArenaShot observedDamage = blue.Shots[^1];
        Require(firstDamage.ShooterPlayerId == red.PlayerId && firstDamage.Outcome == ArenaShotOutcome.Damage &&
            firstDamage.OriginMeters.Z < -11f && firstDamage.EndMeters.Z > 11f &&
            observedDamage.ShooterPlayerId == firstDamage.ShooterPlayerId && observedDamage.ServerTick == firstDamage.ServerTick &&
            observedDamage.OriginMeters == firstDamage.OriginMeters && observedDamage.EndMeters == firstDamage.EndMeters &&
            observedDamage.Outcome == ArenaShotOutcome.Damage, "The server replicates its confirmed damaging ray to both clients");
        red.Shoot(Vector3.UnitZ);
        match.Advance();
        Require(blue.Player(blue).Health == MatchState.MAX_HEALTH - SHOT_DAMAGE, "Server enforces shot interval");
        Require(red.Shots.Count == 2 && blue.Shots.Count == 2 && red.Rays[^1] == "Raycast miss", "Cooldown preserves the legacy reply without broadcasting a shot");

        for (uint sequence = 0; sequence < 8; sequence++)
        {
            ally.Move(sequence, 0, -1);
            match.Advance();
        }
        for (uint sequence = 8; sequence < 40; sequence++)
        {
            ally.Move(sequence, -1, 0);
            match.Advance();
        }
        ally.Move(40, 0, 0);
        match.Advance();
        Require(match.Position(ally).X < -7.5f, "The second shooter reaches the open west route");
        ally.Shoot(Vector3.Normalize(match.Position(blue) - match.Position(ally)));
        match.Advance();
        Require(blue.Player(blue).Health == MatchState.MAX_HEALTH - 2 * SHOT_DAMAGE, "A second enemy contributes damage");
        Require(blue.Shots[^1].ShooterPlayerId == ally.PlayerId && blue.Shots[^1].Outcome == ArenaShotOutcome.Damage &&
            ally.Shots.Count == 0 && ally.Rays.Count > 0, "A shooter can replicate firing without requesting the event stream");

        for (int hit = 2; hit < MatchState.MAX_HEALTH / SHOT_DAMAGE; hit++)
        {
            match.Advance(SHOT_INTERVAL_TICKS);
            red.Shoot(Vector3.UnitZ);
            match.Advance();
        }

        Require(blue.Player(blue).Health == 0, "Fatal hit reaches zero health");
        Require(red.Player(red).Kills == 1 && blue.Player(blue).Deaths == 1, "Server records kill and death once");
        Require(ally.Player(ally).Assists == 1 && red.Player(red).Assists == 0 && blue.State!.RedScore == 1,
            "The server awards the other attacker one assist and the team one point");
        foreach (Peer peer in new[] { red, blue, ally, blueAlly })
        {
            KillEvent kill = peer.Kills.Single();
            Require(kill.AttackerId == red.PlayerId && kill.VictimId == blue.PlayerId &&
                kill.AttackerNickname == "빨강" && kill.VictimNickname == "파랑", "One kill feed event reaches each player");
        }

        blue.Shoot(-Vector3.UnitZ);
        match.Advance();
        Require(red.Player(red).Health == MatchState.MAX_HEALTH, "A dead player cannot shoot");
        match.Advance(RESPAWN_CHECK_TICKS);
        Require(blue.Player(blue).Health == MatchState.MAX_HEALTH && blue.Player(blue).Nickname == "파랑",
            "Respawn restores health under the same identity");
        Require(match.Position(blue).Z > 11.9f && blue.Player(blue).Deaths == 1, "Respawn returns to team spawn and retains deaths");
        Require(red.Kills.Count == 1 && blue.Kills.Count == 1 && ally.Kills.Count == 1, "Respawn does not repeat the kill event");

        for (int score = 2; score <= WINNING_SCORE; score++)
        {
            for (int hit = 0; hit < MatchState.MAX_HEALTH / SHOT_DAMAGE; hit++)
            {
                red.Shoot(Vector3.UnitZ);
                match.Advance(SHOT_INTERVAL_TICKS);
            }
            Require(blue.State!.RedScore == score && blue.Player(blue).Deaths == score, "Each enemy kill adds exactly one point");
            if (score < WINNING_SCORE)
            {
                Require(blue.State.Phase == MatchPhase.Playing, "Combat continues until the winning point");
                match.Advance(RESPAWN_CHECK_TICKS);
            }
        }

        foreach (Peer peer in new[] { red, blue, ally, blueAlly })
        {
            Require(peer.State!.Phase == MatchPhase.Finished && peer.State.Winner == TeamId.Red &&
                peer.State.RedScore == WINNING_SCORE && peer.State.BlueScore == 0, "Both teams see the same final score and winner");
        }

        Vector3 winnerPosition = match.Position(red);
        red.Move(0, 1, 0);
        red.Shoot(Vector3.UnitZ);
        match.Advance(SHOT_INTERVAL_TICKS);
        Require(Vector3.Distance(winnerPosition, match.Position(red)) < 0.02f &&
            red.State!.RedScore == WINNING_SCORE && red.Kills.Count == WINNING_SCORE, "Finished match freezes input and scoring");

        match.Disconnect(blueAlly);
        match.Advance();
        Require(red.State!.Players.Count == 3 && red.State.Winner == TeamId.Red, "Departure removes the player and preserves the result");
        match.Join("Late", RoomJoinResult.WorldUnavailable);
        match.Disconnect(red);
        match.Disconnect(blue);
        match.Disconnect(ally);
        match.Advance();
        Peer next = match.Join("새 경기");
        match.Advance();
        Require(next.State!.Phase == MatchPhase.Waiting && next.State.RedScore == 0 && next.State.BlueScore == 0 &&
            next.State.Winner == null, "An empty room resets for the next round");
    }

    private static void VerifyCover()
    {
        const float OUTSIDE_RED_WALL_Z_METERS = -19.75f;
        using var match = new Arena(teamSize: 1);
        Peer red = match.Join("Red", receiveArenaShots: true);
        Peer blue = match.Join("Blue");
        match.Advance();
        for (uint sequence = 0; sequence < 35; sequence++)
        {
            red.Move(sequence, 1, 0);
            blue.Move(sequence, 1, 0);
            match.Advance();
        }

        red.Move(35, 0, 0);
        blue.Move(35, 0, 0);
        match.Advance();
        Require(MathF.Abs(match.Position(red).X) < 1 && MathF.Abs(match.Position(blue).X) < 1, "Both players are behind central cover");
        red.Shoot(Vector3.UnitZ);
        match.Advance();
        Require(blue.Player(blue).Health == MatchState.MAX_HEALTH && red.Kills.Count == 0, "Blender cover stops damage");
        ArenaShot impact = red.Shots.Single();
        Require(impact.Outcome == ArenaShotOutcome.Terrain && impact.EndMeters.Z > impact.OriginMeters.Z && impact.EndMeters.Z < 11f,
            "A terrain impact ends on cover before the opponent");

        match.Advance(SHOT_INTERVAL_TICKS);
        float outsideCameraOffsetZMeters = OUTSIDE_RED_WALL_Z_METERS - match.Position(red).Z;
        red.Shoot(Vector3.UnitZ, new Vector3(0, 1, outsideCameraOffsetZMeters));
        match.Advance();
        ArenaShot occluded = red.Shots[^1];
        Require(occluded.Outcome == ArenaShotOutcome.Miss && occluded.OriginMeters == occluded.EndMeters,
            "A camera outside the wall cannot produce a visible tracer through it");

        match.Advance(SHOT_INTERVAL_TICKS);
        red.Shoot(Vector3.UnitY);
        match.Advance();
        ArenaShot miss = red.Shots[^1];
        Require(miss.Outcome == ArenaShotOutcome.Miss && miss.EndMeters.Y > miss.OriginMeters.Y + 90f,
            "An unobstructed miss traces the server's tested direction");
    }

    private static void VerifyTraining()
    {
        using var match = new Arena(teamSize: 1, isTrainingRoom: true);
        Peer red = match.Join("훈련");
        match.Advance();
        Require(red.Ack!.Value.MapId == WorldMapId.TeamDeathmatch && red.State!.Phase == MatchPhase.Playing,
            "A single named player starts training with the shared map and protocol");
        Vector3 spawn = match.Position(red);
        red.Move(0, 0, 1);
        match.Advance();
        Require(match.Position(red).Z > spawn.Z + 0.1f, "A lone training player can move");
        red.Move(1, 0, 0);

        Peer blue = match.Join("연습 상대");
        var participants = new List<Peer> { red, blue };
        for (int index = participants.Count; index < MatchState.MAX_PLAYERS; index++)
        {
            participants.Add(match.Join($"동료 {index + 1}"));
        }
        match.Join("초과", RoomJoinResult.WorldUnavailable);
        match.Advance();
        Require(red.State!.Players.Count == MatchState.MAX_PLAYERS &&
            red.State.Players.Count(player => player.Team == TeamId.Red) == MatchState.MAX_TEAM_SIZE &&
            red.State.Players.Count(player => player.Team == TeamId.Blue) == MatchState.MAX_TEAM_SIZE,
            "Training uses the full balanced arena capacity independently of competitive team size");

        const int TRAINING_KILLS = WINNING_SCORE + 1;
        for (int score = 1; score <= TRAINING_KILLS; score++)
        {
            for (int hit = 0; hit < MatchState.MAX_HEALTH / SHOT_DAMAGE; hit++)
            {
                red.Shoot(Vector3.UnitZ);
                match.Advance(SHOT_INTERVAL_TICKS);
            }
            Require(blue.State!.Phase == MatchPhase.Playing && blue.State.Winner is null && blue.State.RedScore == score &&
                blue.Player(blue).Health == 0, "Training retains scoring and stays playable beyond the competitive winning score");
            match.Advance(RESPAWN_CHECK_TICKS);
            Require(blue.Player(blue).Health == MatchState.MAX_HEALTH && match.Position(blue).Z > 11.9f &&
                blue.Player(blue).Deaths == score, "Training respawns the same player at the team spawn after every death");
        }

        foreach (Peer participant in participants)
        {
            match.Disconnect(participant);
        }
        match.Advance();
        Peer newcomer = match.Join("새 훈련");
        match.Advance();
        Require(newcomer.State!.Phase == MatchPhase.Playing && newcomer.State.RedScore == 0 && newcomer.State.BlueScore == 0,
            "A new solo training player starts with an empty scoreboard after the room clears");
        Console.WriteLine("PASS training room: solo start, ten-player capacity, persistent scoring and respawn, empty-room reset");
    }

    private static void VerifyLatency()
    {
        using var match = new Arena(teamSize: 1);
        Peer red = match.Join("Ping");
        match.Advance();
        long token = red.LatencyToken ?? throw new Exception("Missing first latency probe.");
        Require(red.Player(red).PingMs == MatchState.UNKNOWN_PING_MS, "Ping is unknown before the matching reply");
        red.Send(PingCodec.EncodeReply(token == long.MaxValue ? 1 : token + 1));
        match.Advance(3);
        Require(red.Player(red).PingMs == MatchState.UNKNOWN_PING_MS, "A mismatched reply does not set ping");

        red.Send(PingCodec.EncodeReply(token));
        match.Advance();
        int pingMs = red.Player(red).PingMs;
        Require(pingMs is >= 99 and <= 101, "RTT uses elapsed monotonic server time");

        match.Advance(SHOT_INTERVAL_TICKS);
        red.Send(PingCodec.EncodeReply(token));
        match.Advance();
        Require(red.Player(red).PingMs == pingMs, "A replayed reply cannot change an accepted measurement");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private sealed class Arena : IDisposable
    {
        private readonly SessionManager _sessions = new();
        private readonly ManualTimeProvider _clock = new();
        private readonly RoomManager _rooms;
        private readonly GameWorld _world;
        private readonly List<Peer> _peers = new();

        internal Arena(int teamSize, bool isTrainingRoom = false)
        {
            _rooms = new RoomManager(_sessions, 1, 32, 32, _clock,
                teamDeathmatchRoomId: isTrainingRoom ? null : ARENA_ROOM_ID, teamDeathmatchTeamSize: teamSize,
                trainingRoomId: isTrainingRoom ? ARENA_ROOM_ID : null);
            _world = _rooms.GetWorlds().Single();
        }

        internal Peer Join(string nickname, RoomJoinResult expected = RoomJoinResult.Success, bool receiveArenaShots = false)
        {
            var peer = new Peer(new EmbeddedChannel(new GameChannelHandler(_sessions, _rooms)));
            _peers.Add(peer);
            peer.Send(MatchCodec.EncodeJoinRequest(ARENA_ROOM_ID, nickname, receiveArenaShots));
            peer.Drain();
            Require(peer.Result == expected, "Named combat admission returns the expected result");
            return peer;
        }

        internal void Disconnect(Peer peer)
        {
            _peers.Remove(peer);
            peer.Channel.FinishAndReleaseAll();
        }

        internal void Advance(int ticks = 1)
        {
            for (int tick = 0; tick < ticks; tick++)
            {
                _clock.Advance(TimeSpan.FromSeconds(TICK_SECONDS));
                _world.Tick(TICK_SECONDS);
                foreach (Peer peer in _peers)
                {
                    peer.Drain();
                }
            }
        }

        internal Vector3 Position(Peer peer) => _world.GetPlayers().Single(player => player.PlayerId == peer.PlayerId).Position;

        public void Dispose()
        {
            foreach (Peer peer in _peers)
            {
                peer.Channel.FinishAndReleaseAll();
            }
            _world.Shutdown();
        }
    }

    private sealed class Peer(EmbeddedChannel channel)
    {
        internal EmbeddedChannel Channel { get; } = channel;
        internal RoomJoinResult? Result { get; private set; }
        internal JoinAck? Ack { get; private set; }
        internal MatchState? State { get; private set; }
        internal long? LatencyToken { get; private set; }
        internal List<KillEvent> Kills { get; } = new();
        internal List<ArenaShot> Shots { get; } = new();
        internal List<string> Rays { get; } = new();
        internal long PlayerId => Ack!.Value.PlayerId;

        internal MatchPlayer Player(Peer peer) => State!.Players.Single(player => player.PlayerId == peer.PlayerId);

        internal void Shoot(Vector3 direction, Vector3? offset = null) =>
            Send(ActionCodec.EncodeRay(new CameraRay(offset ?? Vector3.UnitY, direction)));

        internal void Move(uint sequence, float x, float z) => Send(PacketCodec.EncodeMoveInput(new MoveInput(sequence, x, z)));

        internal void Send(byte[] frame) => Channel.WriteInbound(Unpooled.WrappedBuffer(frame));

        internal void Drain()
        {
            Channel.RunPendingTasks();
            while (Channel.ReadOutbound<IByteBuffer>() is { } buffer)
            {
                try
                {
                    var frame = new byte[buffer.ReadableBytes];
                    buffer.ReadBytes(frame);
                    byte[] payload = frame[PacketCodec.HEADER_SIZE..];
                    switch ((MessageType)frame[2])
                    {
                        case MessageType.JoinRoomResponse:
                            Result = PacketCodec.DecodeJoinRoomResponse(payload).Result;
                            break;
                        case MessageType.JoinAck:
                            Ack = PacketCodec.DecodeJoinAck(payload);
                            break;
                        case MessageType.MatchState:
                            State = MatchCodec.DecodeState(payload);
                            break;
                        case MessageType.KillEvent:
                            Kills.Add(MatchCodec.DecodeKill(payload));
                            break;
                        case MessageType.ArenaShot:
                            Shots.Add(MatchCodec.DecodeShot(payload));
                            break;
                        case MessageType.RaycastResult:
                            Rays.Add(PacketCodec.DecodeEcho(payload));
                            break;
                        case MessageType.LatencyProbe:
                            LatencyToken = PingCodec.DecodeProbe(payload);
                            break;
                    }
                }
                finally
                {
                    buffer.Release();
                }
            }
        }
    }
}
