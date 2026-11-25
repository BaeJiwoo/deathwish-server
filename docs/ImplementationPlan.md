# Implementation Plan

This plan covers the C# game server. Unity development takes place in a separate repository. Use TCP test clients to verify the server's protocol and lifecycle behavior.

Follow the review workflow in [AGENTS.md](../AGENTS.md). Each checkbox represents implementation, verification, and user review acceptance.

Stages 1 through 5 deliver connection, world admission, an admission response, and departure cleanup. Later stages add movement, snapshots, and quantization. The initial rates of 30 Hz and 10 Hz are configurable starting values.

Current player synchronization work is tracked in [GitHub issue #1](https://github.com/BaeJiwoo/deathwish-server/issues/1): admission completion (Stage 5), authoritative movement input (Stage 6), and full world snapshots (Stage 7) are implemented on playerSync. Unity integration assets are provided under integrations/Unity; deployment and live validation in the separate client project remain follow-up work. Issue task checks describe implemented functionality; stage review acceptance remains governed by this plan. Physics and animation extensions remain in DevelopmentRoadmap.md.

## Main integration checkpoint — 2026-11-16

- The user requested merging the current playerSync implementation into main. The integration includes commits through 3689106 (Stages 5–7 and Unity integration assets); no new stage implementation is introduced.
- Linux verification with .NET 10.0.404: ConnectionTests and MockClient.Console builds passed with zero warnings/errors. The complete ConnectionTests executable exited 0 with no skipped checks, including real TCP admission, movement, matching client snapshots, departure, server shutdown, and port reuse.
- These results supersede the earlier Linux/TCP verification gaps below. Historical stage notes retain their original context. Unity live integration, Windows interactive input, standalone clients, and production load targets remain unverified in this merge task. Review checkboxes are not expanded to imply those checks passed.
- Local issue-tracking documentation and the separate 2026-11-15 development record were preserved alongside the remote implementation record. See [the integration log](devlog/2026-11-16.md).

## Stage 1 — Server startup and shutdown

- [x] Implementation and review complete.
- Add the console project, DotNetty dependencies, and GameServer.
- Bind the configured TCP endpoint and release resources on cancellation.
- Review: Startup failures are visible; shutdown releases the listening port.
- Status — Implemented; user accepted the Program.cs and GameServer.cs code review. Remaining verification is tracked below.
- Implementation — .NET 10 console application, DotNetty.Transport 0.7.6, configurable IP address and port, cancellation-driven cleanup of the listener, accepted connections, and event loops.
- Verified on Windows — Build passed with zero warnings and errors. TCP connection, occupied-port failure, cancellation with a connected client, port reuse, and invalid CLI arguments passed.
- Verification environment — NuGet service-index access failed. Packages were downloaded to a temporary local feed for build verification.
- Remaining verification — Interactive Ctrl+C shutdown and Linux execution. The terminal automation ended the host on Ctrl+C, so graceful shutdown was verified through the cancellation token instead.

## Stage 2 — Connection lifecycle

- [ ] Implementation and review complete.
- Add GameChannelHandler and ClientSession, with one handler per connection.
- Assign a unique SessionId and make disconnect cleanup idempotent.
- Review: Two clients connect independently; repeated disconnects are safe.

### Session and room commit checkpoints

Current configuration update: ServerOptions.cs centralizes default address, port, and room count.
ServerOptionsLoader reads the working-directory .env and process environment at startup;
precedence is CLI address/port, environment, file, then defaults. Invalid effective settings or
malformed file syntax fail before networking starts. The file reader does not mutate the process
environment. .env.example is versioned; local .env files are ignored. Configuration changes need
a restart, not a rebuild. See Protocol.md for supported file syntax and launch instructions.
Program passes immutable options to GameServer, which creates rooms 1 through RoomCount before
allocating networking resources. DefaultRoomCount remains 2 to preserve existing startup behavior.
RoomManager validates the count and preallocates rooms in a loop. Existing CLI address/port
overrides remain supported. Match lifecycle, reset, and dynamic room allocation are out of scope.
Earlier checkpoint references to rooms 1 and 2 describe the default configuration.

The current scope introduces room membership before fixed-tick gameplay. Implement and review one checkpoint at a time; commit after acceptance.

1. `feat: add client session lifecycle` - Create a ClientSession on channel activation with a server-generated Guid and its channel. Mark it disconnected exactly once on ChannelInactive. Status: implemented, awaiting user review. No registry or room state in this checkpoint.
2. `feat: add session registry` - Register, look up, and remove sessions; verify shutdown leaves no registered sessions. Status: implemented, awaiting user review.
3. `feat: add room membership management` - Add rooms 1 and 2, one room per session, and synchronized admission/departure cleanup. Status: implemented, awaiting user review.
4. `feat: define room join protocol` - Specify and encode room join requests and result codes. Status: implemented, awaiting user review.
5. `feat: handle room join requests` - Connect protocol handling to membership and return admission results. Status: implemented, awaiting user review.
6. `feat: add room join command to mock client` - Add JoinRoomAsync and `/join` with TCP verification. Status: implemented, awaiting user review.

Room policy: reject missing rooms and switching rooms; acknowledge same-room requests without duplicate membership. Keep empty rooms. Remove membership before unregistering a disconnected session. Room admission does not create a simulated player; Stage 4 will handle world admission through the world loop.

Checkpoint 6 implementation: ConnectionTestClient exposes JoinRoomAsync and shares request lifecycle handling with EchoAsync. Only one request may be outstanding; response type, payload, and requested room ID are validated. Valid admission failures are returned without closing the connection. Cancellation, malformed/mismatched responses, and I/O errors close it. Console supports interactive and one-shot `/join <positive roomId>` with five-second deadlines and visible result codes. Invalid interactive commands do not disconnect; rejected one-shot requests exit with code 1.

Checkpoint 6 verification: Tests and Console builds passed with zero warnings/errors; the full ConnectionTests executable exited with code 0. Real TCP tests used the production GameChannelHandler/SessionManager/RoomManager on a DotNetty listener: two clients shared room 1, duplicate admission succeeded, switching and missing rooms were rejected, Echo remained usable, and listener/channel-group/event-loop shutdown left no sessions or room members. Scripted TCP peers verified exact request bytes, split responses, wrong response types/room IDs, malformed results/lengths, overlapping requests, timeout, EOF, and truncated responses. The actual GameServer executable and Console also passed an interactive command sequence and one-shot success/rejection exit-code checks. That CLI smoke-test server process was terminated after testing; full GameServer.RunAsync cancellation and Unity integration remain unverified.

Checkpoint 5 implementation: GameChannelHandler dispatches EchoRequest and JoinRoomRequest. Join requests use the handler's session identity and RoomManager.Join; the response contains the requested room ID and admission result. The existing write helper is shared as SendResponseAsync. Valid admission failures keep the connection open; malformed payloads, wrong message direction, and failed writes close it and trigger membership/session cleanup. No socket I/O occurs under the room lock. MockClient changes remain in checkpoint 6.

Checkpoint 5 verification: Build passed with zero warnings and errors; the full ConnectionTests executable exited with code 0. New EmbeddedChannel checks passed for successful and duplicate admission, missing rooms, rejected room switching, two independent senders, mixed Echo/join response ordering, every request split point, malformed lengths/IDs, response-direction rejection, disconnect cleanup, and injected response-write failure cleanup. Existing protocol, room concurrency, Echo, and TCP client lifecycle checks passed. Real TCP room round trips remain for checkpoint 6; output queue limits remain deferred.

Checkpoint 4 implementation: Shared JoinRoomRequest (type 3) carries a positive int32 RoomId. JoinRoomResponse (type 4) carries the requested RoomId and one result byte. RoomJoinResult moved from Server to Protocol with explicit stable values 0 through 3. PacketCodec encodes complete frames and decodes exact-size payloads using big-endian integers; invalid IDs, lengths, and result codes are rejected. EncodeEcho now rejects non-Echo types. Protocol.md defines layouts, failure semantics, examples, and compatibility. PacketReader recognizes the new types, but endpoint dispatch and client requests remain deferred to checkpoints 5 and 6.

Checkpoint 4 verification: Build passed with zero warnings and errors. ConnectionTests exited with code 0. New protocol checks passed for exact wire bytes, all result codes, minimum/maximum IDs, every split point, mixed combined frames, and rejection of malformed payloads and invalid encoder arguments. Existing session, room concurrency, Echo, and TCP client lifecycle tests passed. Actual room request/response networking is not implemented in this checkpoint.

Checkpoint 3 implementation: Each server run creates a RoomManager using its SessionManager, then shares both with connection handlers. Rooms 1 and 2 remain available when empty. RoomManager owns the session-to-room map and serializes Join/Leave with room membership updates under one lock. Room exposes its ID, member count, and copied member IDs. No second membership field is stored on ClientSession. Unknown or disconnected sessions cannot join; missing rooms and attempts to switch rooms return explicit results. Same-room joins return success without duplicate membership. Leave is idempotent and permits later admission while still connected. ChannelInactive marks the session disconnected before removing room membership, then unregisters the session. A join overlapping disconnect may succeed before cleanup, but cannot leave residual membership after cleanup completes. No socket I/O occurs under the membership lock.

Checkpoint 3 verification: Build passed with zero warnings and errors. ConnectionTests exited with code 0, including admission rules, immutable member snapshots, departure/rejoin, disconnect and protocol-error cleanup, room cleanup on channel-group shutdown, and 32 iterations of simultaneous admissions and admission/disconnect races. Existing Echo and TCP client lifecycle checks passed. Room tests use EmbeddedChannel and direct server API calls; room packets, console join commands, capacity limits, gameplay, and real TCP room admission are outside this checkpoint.

Checkpoint 2 implementation: Each GameServer.RunAsync invocation owns one SessionManager shared by its connection handlers. ConcurrentDictionary protects cross-connection registration, lookup, and removal. The handler registers a fresh session on activation and removes it on its first inactive notification. Mutation methods are internal; public access is limited to Count and TryGetSession. Lookup does not guarantee that the connection remains open after returning. Existing channel-group shutdown closes connections and drives removal; the registry is not cleared to conceal missing lifecycle callbacks.

Checkpoint 2 verification: Single-node build with shared compilation disabled passed with zero warnings and errors. The complete ConnectionTests executable exited with code 0. Checks covered lookup, registration counts, duplicate activation/disconnection, protocol-error cleanup, eight-channel group shutdown, 32 parallel connection lifecycles, existing Echo framing, and TCP client closure/timeout behavior. Group shutdown was tested with EmbeddedChannel; full GameServer shutdown over real TCP and Unity integration remain unverified.

Checkpoint 1 verification: `dotnet build tests/DeathWish.ConnectionTests --no-restore -m:1 -nr:false` passed with zero warnings and errors. EmbeddedChannel checks passed for distinct session IDs, channel association, repeated activation, independent disconnection, repeated close/inactive notifications, and protocol-error disconnection. Existing in-memory Echo regression checks also passed. The TCP lifecycle checks reported `SKIP` because the environment denied socket connections; real TCP session lifecycle and Unity verification remain pending. The initial default parallel build failed without compiler diagnostics; the single-node build above succeeded.

## Stage 3 — Fixed tick execution

- [x] Implementation and review complete.
- Design specification — [World tick class specification](WorldTickDesign.md) defines per-room GameWorld, shared WorldLoop, statistics, configurable SERVER_TICK_RATE, lifecycle, and review criteria.
- Status — Implemented and verified on Linux. The user finished code review with no requested changes and explicitly authorized Stage 4; Stage 3 review is accepted. Git history confirms commit ffea8e3 (`feat: add configurable fixed-tick world loop`).
- Add WorldLoop and GameWorld.Tick with an initial rate of 30 Hz.
- Use monotonic elapsed time and a fixed simulation delta.
- Bound catch-up work and record tick overruns.
- Review: Ticks never overlap; cancellation stops the loop cleanly.
- Implementation — Each room owns a GameWorld. A single-use WorldLoop updates worlds in room ID order outside DotNetty event loops, with fixed simulation delta and rational monotonic deadlines. Each catch-up batch runs at most four ticks; excess deadlines are skipped without advancing simulation counters. Timer waits round up to at least 1 ms, including after backlog is dropped. Immutable statistics report completed ticks, processing overruns, duration, and dropped time.
- Configuration — ServerOptions.TickRate defaults to 30. SERVER_TICK_RATE resolves process environment, .env, then default; a positive int32 is required and changes take effect after restart. Existing CLI address/port syntax remains unchanged.
- Lifecycle — GameServer starts the loop only after binding, observes cancellation and loop failure, closes the listener, awaits the loop, then closes connections and event loops. All cleanup operations are attempted; original failures are preserved or aggregated. An internal TimeProvider constructor supports deterministic integration checks.
- Verified on Linux — .NET 10.0.404 builds of ConnectionTests and MockClient.Console passed with zero warnings/errors; the full ConnectionTests executable exited 0. New checks cover first-tick timing, 30 Hz scheduling over 300 ticks, fixed delta, copied/ordered worlds, concurrent-start rejection, bounded catch-up, sustained overload, statistics, high-rate yielding, cancellation, partial-tick failure, and loop fault propagation. Existing protocol, session, room, and TCP client checks passed.
- Integration verification — Actual GameServer runs with two connected clients passed admission, Echo, cancellation, simultaneous loop fault/cancellation, disconnect, occupied-port failure, and port reuse. A separate production executable smoke test loaded an isolated .env with 3 rooms and 60 Hz, returned exact Echo bytes, advanced 14 ticks, and exited 0 on SIGINT with client EOF and port reuse. Existing TCP room tests directly assert empty session/member registries after shutdown; the GameServer tests observe public socket lifecycle rather than accessing its local registries.
- Remaining limitations — World ticks currently advance counters only. Player state, commands, and gameplay remain Stage 4 and later. One slow room delays every room; no performance target, Windows run, or Unity integration has been verified for this change. The default host SDK is .NET 8, so verification used an isolated /tmp/deathwish-dotnet10 SDK without changing project targets.

## Stage 4 — World admission and removal

- [x] Implementation and review complete.
- Add WorldCommand and PlayerState; only the world loop mutates player state.
- Queue Join and Leave in connection order and assign PlayerId on admission.
- Bound admission and per-tick command processing; guarantee departure cleanup.
- Define queue overflow behavior so a full admission queue cannot discard departure cleanup. Ignore commands for departed sessions.
- Review: Immediate disconnect, duplicate Leave, and shutdown leave no players behind.
- Status - Implemented and verified on Linux; user explicitly authorized Stage 5 on 2026-11-15, accepting the Stage 4 review checkpoint. Git history confirms 0b49e0a (feat: add tick-based world admission and departure).
- Design — [World admission and departure](WorldAdmissionDesign.md) defines bounded admission reservations, per-tick processing, player IDs, snapshots, and terminal cleanup.
- Implementation — RoomManager serializes admission/leave with membership changes. Each accepted admission has a distinct token and reserves space for one Join and one Leave. Defaults are 1024 outstanding admissions and 128 processed commands per room per tick; queued commands are bounded by 2048. Capacity includes pending, active, and departing admissions until Leave is consumed. Full capacity returns WorldUnavailable without adding membership. Stale Join commands are skipped but consume processing budget; duplicate or old Leaves cannot remove a rejoined player.
- Player state — GameWorld alone assigns increasing positive long PlayerIds within a world run and maintains immutable PlayerState values with SessionId and initial Vector3.Zero position. Public readers receive copied snapshots; room members and world players may temporarily differ while commands wait. No movement or physics is implemented.
- Protocol — JoinRoomResponse adds WorldUnavailable=4; values 0–3 and layouts are unchanged. Success means reservation accepted, not player creation completed. Older codecs reject value 4 and must be updated. JoinAck with PlayerId/tick/spawn remains Stage 5.
- Shutdown — WorldLoop finally closes every world command queue and clears players/reservations on cancellation, pre-cancellation, and failure. Shutdown cleanup is terminal, outside the per-tick budget, and runs on the world owner. Later channel departure callbacks become harmless no-ops for closed queues. GameServer stops admission and also finalizes worlds if binding failed before loop startup.
- Verified on Linux — ConnectionTests and MockClient.Console builds passed with zero warnings/errors using .NET 10.0.404. The full test executable exited 0. Checks cover deferred creation, duplicate admission, snapshot isolation, same-session rejoin ordering, active/pending capacity, full-queue departures, stale joins, old departure tokens, 32 concurrent join/tick/disconnect races, pre-cancelled/normal/failing loop cleanup, and stopped-world rejection. Real TCP tests assert two distinct players after ticking and zero players/reservations after departure. Protocol tests round-trip result 4 and reject unknown values; MockClient accepts WorldUnavailable without disconnecting. Existing tick, server lifecycle, session, room, and Echo tests passed.
- Remaining limitations — Capacity/budget are code defaults, not runtime options. A slow world still delays other rooms. Terminal cleanup is bounded by admitted capacity but does not enforce the ordinary per-tick budget. Stage 5 notification, production load targets, Windows, and Unity integration remain pending.

## Stage 5 — Binary admission response

- [x] Implementation and review complete.
- Create `docs/Protocol.md` with frame length semantics, message IDs, byte order, maximum frame size, field layouts, and malformed-packet behavior.
- Use DotNetty length framing and PacketCodec for JoinAck payloads.
- Return PlayerId, ServerTick, and spawn position after world admission.
- Send through ClientSession without awaiting socket I/O in the world loop.
- Define buffer ownership and handle failed writes and slow connections.
- Keep reliable responses ordered and bound pending output. Disconnect a session when it exceeds the defined output limit.
- Review: A TCP test client decodes the exact documented bytes.
- Review: Split frames, combined frames, and invalid lengths are handled correctly.

- Status - User authorized Stage 6 on 2026-11-15 after the Stage 5 review discussion. Review accepted; the real TCP verification limitation remains tracked below. Git history confirms cca8223 (feat: add world admission acknowledgements and bounded session output).
- Implementation - JoinAck type 5 carries room ID, player ID, creation tick and unquantized spawn position. Creation emits one notification; duplicate room requests only repeat the reservation response. Session output ordering guarantees the initial reservation response precedes JoinAck.
- Output - All reliable responses use one session FIFO, bounded to 64 frames/256 KiB including one in-flight write. Overflow, failed writes and a five-second write timeout close the connection. World ticks do not await socket completion. Production adds DotNetty.Codecs 0.7.6 length framing.
- Client - MockClient receives asynchronous JoinAck alongside request responses; WaitForAdmissionAsync and /join expose the actual admission state. Malformed, duplicate, unsolicited and mismatched admission messages close the client.
- Verification on Windows - .NET 10.0.404 builds succeeded. ConnectionTests reported NU1900 warnings because NuGet vulnerability data was inaccessible; Console build had zero warnings/errors. In-memory checks passed for exact bytes, every split point, combined frames, malformed payloads, production length decoding, world ordering, duplicate join, output overflow, timeout and cleanup, plus existing regression checks. Full runner exited 2: real TCP connection denied with SocketException 10013. TCP assertions were added but are not claimed as passed.
- Remaining - Run ConnectionTests and the Console /join command in an environment allowing loopback TCP; Unity, Linux Stage 5 validation and production load targets remain unverified. Output limits are code defaults; movement and snapshots remain Stages 6 and 7.

## Stage 6 — Server-authoritative movement

- [ ] Implementation and review complete.
- Add MoveInput with an input sequence and movement direction.
- Document movement speed, coordinate axes, direction normalization, sequence comparison including wraparound, and the stale-input timeout in `docs/Protocol.md`.
- Validate finite values and direction bounds; associate input with its session.
- Keep the latest movement state per player and stop stale input after a timeout.
- Review: Packet frequency does not change movement speed; stale input stops movement.

- Status - User authorized the WASD client and snapshot work on 2026-11-15. Stage 6 code review acceptance has not been explicitly recorded; it remains unchecked.
- Protocol - MoveInput type 6 carries uint32 sequence and float32 X/Z direction. Codec rejects nonfinite/out-of-range components and wrong lengths. Session identity selects the admission; no client position or time is trusted. No input response is sent.
- Simulation - 5 m/s maximum on XZ, clamping direction length to 1 while preserving smaller analog values. Join/Leave processing precedes one movement update per active player per tick. Updated PlayerState snapshots are copied and published by the world owner.
- Input storage - One synchronized latest-input slot per admission replaces a movement command queue. Storage stays bounded by admission capacity; movement cannot crowd out Leave. Pending admissions retain receipt age; departed/closed slots reject input, and rejoin starts fresh.
- Ordering and expiry - First sequence arbitrary, subsequent unchecked uint32 distance must be in (0, 2^31); duplicates/old/ambiguous input neither replace state nor refresh age. Monotonic receipt age >=250 ms stops movement without clearing sequence history. GameServer passes the same TimeProvider to the world loop and admission slots.
- Client - SendMoveAsync after JoinAck sends without awaiting a reply and shares an operation guard with Echo/join. Interactive /move x z increments sequence and sends one sample; held-input refreshing is up to the caller.
- Verification - Windows .NET 10.0.404 builds passed; deterministic/in-memory tests cover wire bytes, splits/combined frames, invalid floats and lengths, normalized/analog speed, packet-frequency independence, stop, exact expiry, wraparound, pending admission, delayed departure, rejoin, shutdown, sender isolation and concurrent input/tick processing. Real TCP movement assertions were added to RoomClientTests, but loopback connection remains denied (10013); full test runner exits 2 (SKIP). NuGet vulnerability metadata retrieval reports NU1900; no compile errors.
- Remaining - Real TCP, Linux and Unity validation remain unverified. No physics, map limits, acceleration, transport input-rate limiter or runtime movement options. No position packets/broadcast until Stage 7. Constants touched in PacketCodec/GameWorld now use UPPER_SNAKE_CASE, with call sites updated.

## Stage 7 — World snapshots

- [ ] Implementation and review complete.
- Add WorldSnapshot with ServerTick and player states; start at 10 Hz.
- Specify full-snapshot semantics for player creation and removal on the client.
- Document snapshot field layouts and ordering after JoinAck in `docs/Protocol.md`.
- Bound pending sends and coalesce snapshots that have not started transmission.
- Review: Two TCP test clients decode matching membership and positions for the same server tick without blocking ticks.

- Status - Implemented with explicit user authorization to extend through MockClient and Unity movement; awaiting review.
- Server - Full type-7 room snapshots at 10 Hz simulation time (capped by tick rate), after lifecycle and movement; ordered player IDs, immutable shared encoding, reliable responses before first snapshot, one coalesced unsent snapshot per connection within existing output bounds.
- Wire - Up to 1024 players require a snapshot-only maximum payload of 20,494 bytes. Existing request and Echo bounds remain unchanged. New clients must be deployed with this server.
- Clients - Core exposes the latest immutable snapshot. Windows Console /wasd polls held keys at about 30 Hz and supports arrow-key yaw, focus-loss stop and position display. It requires a standalone Console Host window (launcher provided). Console and Unity share bounded rotated-input conversion. Unity's saved SyncTestScene uses a preplaced Canvas with Inspector-wired buttons and local server X/Y/Z. The Game view renders blue local and yellow remote capsules on a white lab platform, with a third-person camera following the local player. Mouse X/Y orbits the camera and WASD uses camera yaw. Scene-view Gizmos still show exact positions and IDs. Departures remove visuals. The later gravity integration uses Bepu on the server. Unity blends both local and remote Game-view markers over 100 ms and follows the rendered local position with the camera.
- Integration boundary - The original Unity project's opening/package errors were repaired and its previous movement screen was Play-mode verified. The new SyncTestScene revision is prepared under integrations/Unity, but remains unapplied to the original sibling project because it is outside the writable workspace. The installer backs up old auto-start scripts, adds the installed Unity 6 uGUI 2.0.0 package, and preserves scene customizations on reinstall. No review acceptance is inferred.
- Scene verification - Unity API compile check passed with zero warnings/errors, including editor Gizmo code. Windows PowerShell 5.1 installer regression passed. In a disposable project opened with actual Unity 6000.3.11f1, scene import verified Canvas references, fonts and button wiring. Play-mode checks passed local coordinates, telemetry, remote departure and disconnect reset using synthetic snapshots; local/remote Gizmos and the Canvas were visually confirmed. Corrected Unity 6 LegacyRuntime font references to file ID 10102 after the initial import exposed invisible text. Live TCP movement, focus behavior, two-client synchronization and standalone builds remain unverified.
- Verification - In-memory snapshot checks cover exact bytes, split/combined packets, maximum capacity, malformed state, room isolation, equal snapshots, departure, movement ordering, cadence and stalled-output coalescing. TCP equality assertions are added but environment connection denial still prevents execution. Added regression checks for rotated client input and Windows PowerShell 5.1 manifest repair/reinstallation. See today's development log for final build/test results. Follow [Testing.md](Testing.md) for the VS Code, Console and Unity manual review steps and expected results.


## Player gravity and floor collision (issue #2)

Review each unit before committing it and starting the next unit.

1. Gravity integration and server configuration — approved and committed. Configure the downward acceleration magnitude with `SERVER_GRAVITY_METERS_PER_SECOND_SQUARED` (default 9.81, finite and nonnegative). Unit 4 transfers vertical velocity and integration to the Bepu body.
2. Generic box and finite horizontal plane colliders — approved and committed. Both carry their own world center and dimensions and expose world bounds through Collider. Mixed-list tests cover independent placement and movement. Player dimensions and placement belong to the integration step.
3. Per-world collider list and origin floor — approved and committed. Each world creates one 100 m × 100 m plane at Y = 0 (expanded after the original floor review). Room creation tests verify bounds and independent list and floor instances.
4. BepuPhysics movement and floor contact — implemented, awaiting review. Replace the uncommitted custom sweep with an isolated server simulation using BepuPhysics 2.4.0. Drive an upright player box by horizontal velocity with engine-integrated gravity. Create the simulation from the room gravity setting and its static collider list. Register each box as a Bepu box and each plane as two upward-facing triangles. Enable continuous detection on the player. Verify walking, stopping, high-speed landing, edge support and falling without input at 30 Hz.
5. Room tick integration — implemented at user request, awaiting review. Create the room simulation on first admission, add/remove player bodies, apply horizontal velocities, step once per occupied tick, publish foot positions and dispose on shutdown. Two TCP clients verified matching authoritative positions after walking off the floor and stopping horizontal input in midair.

## Stage 8 — Position quantization

- [ ] Implementation and review complete.
- Define units, range, rounding, overflow handling, and protocol compatibility.
- Update `docs/Protocol.md` with the quantized layout and reconstruction rules.
- Quantize positions in PacketCodec while retaining float simulation state.
- Review: Boundary values and reconstruction error match the documented contract.

## Stage 9 — Platform and load validation

- [ ] Implementation and review complete.
- Run the same connection, movement, and shutdown scenarios on Windows and Linux.
- Agree on target player count and input rate before judging performance.
- Measure tick duration, overruns, allocations, queue depth, and pending sends.
- Review: Record results and any unverified platform or load targets.

## Connection-test checkpoint before Stage 2

- [ ] Implementation and review complete.
- Add `mockclient/DeathWish.MockClient.Core` and `mockclient/DeathWish.MockClient.Console`.
- Add per-connection server logging and length-prefixed typed Echo handling in `GameChannelHandler`.
- Keep connection-test transport reusable from Unity through .NET Standard 2.1.
- Specify framing, limits, and execution commands in [Protocol.md](Protocol.md).
- Status: Implemented; awaiting user review and runtime verification.
- Build verification: Server, Core, and Console builds passed with zero warnings and errors.
- Verification limitation: Local TCP connection attempts were denied by the execution environment. Echo round trips, concurrent clients, timeout behavior, and disconnect logs require runtime verification.
- Stage 2 will extend the handler with ClientSession and session identity.
- Review fix: Continuously receive in Core and expose `Disconnected` so server closure interrupts console input waiting. Pending Echo requests fail on closure; runtime shutdown verification remains pending in an environment permitting TCP.
- Communication review: Reject duplicate Connect and Echo-before-connect calls, preserve cancellation causes, validate Echo content in Core, and report timeouts explicitly.
- Regression checks passed: fragmented headers/payloads, combined/empty frames, 4096-byte boundary, oversized/invalid UTF-8/type/direction rejection, Echo-before-connect, and Connect-after-disposal. Socket lifecycle checks remain blocked by TCP access denial.

## Deferred design considerations

- [ ] Review resource ownership in `GameServer.RunAsync` before introducing separate lifecycle methods or broadcasting.
  - Keep `acceptGroup` and `connections` local while `RunAsync` owns their creation, use, and cleanup.
  - Reconsider making `acceptGroup` a field if `StopAsync` or `DisposeAsync` needs to manage event-loop shutdown.
  - Reconsider making `connections` a field if other methods need to broadcast or close connections.
  - Before moving either resource to a field, define initialization and cleanup ownership, access before startup and after shutdown, and concurrent or repeated `RunAsync` behavior.

## Binary Echo review update

- Shared DeathWish.Protocol now defines a 3-byte header (big-endian payload length and message type), PacketReader, and Echo codec. This brings basic framing forward from Stage 5; JoinAck remains in Stage 5.
- Server and MockClient use the shared protocol. Maximum payload is 4096 bytes. Old newline clients must be updated.
- Awaiting user review. TCP and Unity runtime verification remain pending.

## PhysicsLab test map (issue #3)

1. Extract the shared static triangle map from Blender — approved and committed (3345fca). See [coordinate contract, reproduction and geometry checks](../maps/PhysicsLab/README.md).
2. Register the selectable map as static Bepu geometry — approved and committed (34ec358). The highest configured room ID selects PhysicsLab. Lower rooms use the flat floor. Spawn, floor, platform opening, ring passage and rim contact passed with server player bodies.
3. Player traversal — implemented, awaiting review. Encoded movement inputs cover both ramp seams in both directions, the platform opening, and head-on and diagonal wall approach. A beveled sole prevents snagging at the ramp toe. Ring contact passed in stage 2.
4. Unity map display — implemented for review in deathwish-client. Live Unity Play mode verified admission to PhysicsLab, mesh counts, and switching back to the flat room.
5. Two clients in the lab now receive distinct nearby spawn positions and render each other in a live Unity camera check. The full connection regression suite passed. The two-client traversal route remains pending.

Review each unit before committing and moving to the next. Stages 2–5 depend on the Bepu server work in #2.

## 5대5 팀 데스매치 PoC

### 목표

Blender에서 만든 경기장 한 장에서 두 팀이 5명씩 접속해 싸우는 로컬 매치를 만든다. 첫 플레이 확인은 Unity 클라이언트 두 개로 한다. 각 플레이어는 입장 전에 이름을 입력하고, 상대의 이름과 팀 색상을 확인하며, 원통형 캐릭터가 든 사각형 총 한 종류로 서로 사격할 수 있다.

- 서버는 입장 이름을 검증하고 플레이어 ID에 연결한다. 빈 이름은 거부하고 한글을 지원한다. Unity는 다른 플레이어의 머리 위에 서버가 전달한 이름을 표시한다.
- 기존 이동과 3인칭 카메라를 사용한다. 서버는 조준 입력과 발사 간격을 검증하고, 맵과 플레이어를 대상으로 명중, 피해, 사망 및 리스폰을 판정한다. 벽과 엄폐물은 사격을 막고 아군에게는 피해를 주지 않는다.
- Unity는 조준점과 현재·최대 체력에 맞춰 갱신되는 내 체력바를 표시한다. 킬로그에는 서버가 확정한 처치의 공격자와 피해자 닉네임을 표시한다.
- 서버가 팀을 자동 배정하며 기본 설정에서 두 팀이 5명씩 모이면 매치를 시작한다. 적 처치당 팀 점수 1점이며 10점 선승이다. 사망 후 3초부터 빈 팀 스폰에서 부활한다. Unity에는 팀 점수와 승리 팀을 표시한다.
- Tab을 누르는 동안 팀별 점수판을 열어 참가자의 닉네임, 킬·데스·어시스트, 핑(ms)을 보여 준다. 처치 직전 5초 안에 피해를 준 다른 공격자는 어시스트를 얻는다. 서버가 측정한 연결별 왕복 시간(RTT)을 공유하며 첫 측정 전에는 핑을 대시로 표시한다.
- 캐릭터 선택·스킬, 탄약·재장전, 계정·매치메이킹, 랭킹, 배포, 지연 보상은 후속 범위로 둔다.

### Blender 맵과 저장소 경계

- [PhysicsLab의 내보내기 방식](../maps/PhysicsLab/README.md)을 재사용한다. Blender에서 단순한 바닥·벽·엄폐물과 팀별로 떨어진 스폰 5개씩을 배치한다. 스폰은 Blender Empty로 표시하고 팀, 위치, 방향을 메시와 함께 버전이 명시된 JSON으로 추출한다.
- 기존 미터 단위, Y-up 월드 좌표, 시계 방향 삼각형 규칙을 따른다. 생성한 JSON은 서버에서 Bepu 충돌에, Unity에서 지형 표시에 사용한다. 방 입장 시 서버가 맵 ID를 전달하고, Unity가 해당 JSON을 선택한다. 기존 평지와 PhysicsLab도 회귀 검증한다.
- `deathwish-server`는 Blender 내보내기·검증, 생성 JSON, 서버 판정과 공유 프로토콜을 다룬다. `deathwish-client`는 Unity 씬, 이름 입력, 캐릭터·총·이름표, 조준과 HUD를 다룬다. 서버 소유 공유 파일과 DLL은 같은 버전으로 Unity에 동기화한다.
- 두 저장소의 로컬 `codex/tdm-poc`에서 진행한다. 서버 기준은 `38edc0a`, 클라이언트 기준은 `e27bd0a`다. 첫 단계에서 서버의 `origin/feat/jump-and-raycast`와 클라이언트 `main`의 레이캐스트 구현 및 패킷 호환성을 확인하고 재사용 범위를 결정한다.

### 구현 및 리뷰 순서

| 단계 | 구현 범위 | 리뷰 기준 |
| --- | --- | --- |
| 1. 공용 경기장 | 서버·클라이언트 통신 기준 확인, Blender 경기장과 스폰 정의, JSON 내보내기, 서버 충돌 및 Unity 표시 | 같은 Blender 원본의 반복 내보내기 결과와 두 런타임의 JSON 해시가 일치한다. 스폰이 서로 또는 지형과 겹치지 않고 Unity에서 보이는 벽과 엄폐물이 서버 이동을 막는다. |
| 2. 두 명의 교전 | 이름 입력·이름표, 팀 색상 캡슐과 사각형 총, 서버 명중·피해·사망·리스폰, 조준점·체력바·킬로그 | Unity 클라이언트 두 개가 서로의 이름과 외형을 보고 사격한다. 엄폐 뒤에서는 피해가 없고, 피격·리스폰에 체력바가 반응한다. 처치가 양쪽 킬로그에 한 번씩 표시되며, 사망한 플레이어는 같은 이름으로 팀 스폰에 다시 등장한다. |
| 3. 5대5 매치 | 정원, 팀 배정, 경기 시작·팀 점수·승리 확정, Tab 점수판과 핑 측정, 로컬 통합 검증 | 한 방에서 TCP 세션 10개가 5명씩 배정되고 초과 입장을 거부한다. 양쪽 Unity의 점수판에 같은 닉네임과 K/D/A가 표시되며, 핑은 각 연결의 측정값을 표시한다. 양쪽의 점수와 승리 팀이 같고, 경기 중 이탈한 플레이어가 제거된다. |

각 단계의 검증 결과를 기록한다. 사용자가 PoC 목표까지 단계별 연속 진행을 요청해 2단계와 3단계를 순서대로 구현하고 리뷰 결과를 각각 남긴다. 마지막 단계의 로컬 검증은 서로 다른 팀의 Unity 클라이언트 2개와 사격·이동이 가능한 테스트 클라이언트 8개로 재현한다. Unity 10인 실플레이를 진행하면 결과를 별도로 기록한다.

1단계는 [Blender 원본과 공유 JSON](../maps/TeamDeathmatch/README.md), 서버의 경기장 선택 및 스폰 배치, Unity 맵 표시를 구현했다. 기존 Unity의 점프와 레이캐스트에 맞춰 대응 서버 입력 처리와 공유 DLL을 동기화했다. 검증 결과와 남은 확인은 [개발 기록](devlog/2026-11-24.md#1단계--공용-경기장)에 기록한다.

2단계는 이름 입장, 교전·리스폰과 Unity HUD를 구현했다. 3단계는 팀별 정원, 경기 상태와 10점 선승, 양쪽 팀 점수판과 RTT 측정을 구현했다. 10개 TCP 세션 회귀 테스트, 두 Unity 클라이언트의 1대1 교전, Unity 2개와 MockClient 8개의 5대5 경기 종료를 검증했다. `SERVER_TDM_TEAM_SIZE=1`로 동일한 매치 흐름을 1대1로 확인할 수 있다. [로컬 검증 방법](Testing.md#11-팀-데스매치-경기장-확인) · [검증 결과](devlog/2026-11-24.md#3단계--5대5-매치와-점수판)

### 경기장 물리 검증 확장

팀 데스매치 경기장 동쪽에 곡면 아치와 경사로가 이어진 외다리를 추가한다. 원본 Blender 메시를 공유 JSON으로 다시 내보내고 서버 Bepu 충돌과 Unity 표시에 같은 데이터를 사용한다. 기존 스폰과 교전 동선을 유지한다.

리뷰에서는 아치 개구부 통과와 측면·천장 충돌, 다리 경사로 접합부와 상하 표면, 발판 가장자리 추락·착지를 확인한다. [경기장 구조](../maps/TeamDeathmatch/README.md#동쪽-물리-검증-경로) · [로컬 검증 순서](Testing.md#14-아치와-외다리-물리-확인)

Blender 경기장과 검증 시나리오를 구현했다. Bepu 서버 물리와 10인 TCP 회귀 테스트, 실제 Unity 1대1 Play Mode, Unity 메시·레이 검증이 통과했다. [확장 검증 기록](devlog/2026-11-24.md#경기장-물리-검증-확장)

### 블록 점프 코스

기본 서버 물리에서 지상 점프의 수평 이동 범위를 측정하고, 서쪽에 높이가 다른 블록 5개를 배치한다. 같은 높이와 0.4 m 높은 착지면을 모두 측정하며, 간격이 2.10 m에서 4.65 m까지 늘어나는 네 점프를 테스트한다. 발판에서 이륙해 다음 상판에 착지하고, 걸어서 벗어나면 바닥에 떨어져야 한다. [점프 측정과 발판 치수](../maps/TeamDeathmatch/README.md#서쪽-블록-점프-코스)

기본 설정에서 수평 거리 6.167 m와 5.833 m를 각각 측정했다. Bepu로 양방향 네 번 연속 점프와 실패 시 바닥 착지, Unity로 공유 메시·빈 간격·실제 1대1을 검증했다. [검증 기록](devlog/2026-11-24.md#서쪽-블록-점프-코스)

### 번호별 순찰 연습장

서버 설정으로 팀 데스매치 맵을 사용하는 연습장 방을 연다. 첫 참가자가 입장하면 이동과 교전을 허용하고, 기존 서버 사망 판정과 팀 리스폰을 사용한다. 점수는 쌓이지만 연습장은 승리 점수에서 종료하지 않는다.

MockClient는 번호 1의 중앙 엄폐물, 번호 2의 동쪽 아치·외다리, 번호 3의 서쪽 점프 코스를 이동·점프로 반복한다. 서버에서 받은 체력과 위치로 사망 및 리스폰을 확인하고 같은 번호의 구역으로 돌아간다. 리뷰에서는 실제 TCP에서 경로별 동작, 처치 후 복귀, 정지와 경로 변경을 확인한다. [연습장 실행과 순찰 명령](Testing.md#16-연습장과-mockclient-순찰)

WSL에서 기존 세 순찰과 봇의 사망 후 복귀를 TCP로 검증했고, Windows Unity에서 연습장 접속을 확인했다. [개발 기록](devlog/2026-11-24.md#번호별-순찰-연습장)

### 사격 표시와 봇 간 교전

Unity에서 캡슐에 사각형 총을 장착한다. 좌클릭에 총구 섬광과 반동이 즉시 나타나고, 서버의 명중 판정과 다른 플레이어의 발사 궤적을 표시한다. 서버는 참가 시 사격 표시를 요청한 클라이언트에만 새 이벤트를 보낸다.

총과 연결 부품은 캐릭터 오른쪽에 고정한다. 팀 데스매치와 연습장 카메라는 화면 비율에 맞춰 몸통을 화면 가장자리 가까이에 배치한다. 리뷰에서는 양쪽 어깨 전환과 벽 근처에서 중앙 조준선 및 총 위치를 확인한다.

4번 봇은 기존 맵 동쪽 통로의 팀별 위치에서 서로 맞은편에 있는 살아 있는 상대를 공격한다. 처치와 리스폰은 서버가 판정하고 봇은 리스폰 뒤 같은 구간으로 복귀한다. 리뷰에서는 Unity의 총과 다른 참가자의 사격 표시, 서버의 명중과 체력 변화, 같은 팀의 피해 방지, 여러 봇의 교전과 리스폰 재합류를 확인한다. [실행 방법](Testing.md#16-연습장과-mockclient-순찰)

### 경기장 확장과 원통형 캐릭터

Blender 경기장의 바닥과 외벽을 32 m × 38 m로 확장해 외곽으로 돌아가는 통로를 만든다. 아치, 외다리, 점프 코스와 스폰 10개의 위치는 유지한다. Blender에서 다시 내보낸 JSON을 서버 충돌과 Unity 표시에 공용으로 사용한다.

캐릭터는 Unity 렌더링과 서버 충돌에서 지름 0.65 m, 높이 1.8 m의 원통을 사용한다. 리뷰에서는 새 외곽의 지지·벽 충돌, 기존 아치·외다리와 블록 점프, 조준·피해·리스폰 및 순찰 경로를 확인한다.
