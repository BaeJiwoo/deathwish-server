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
- Clients - Core exposes the latest immutable snapshot. Windows Console /wasd polls held keys at about 30 Hz and supports arrow-key yaw, focus-loss stop and position display. It requires a standalone Console Host window (launcher provided). Console and Unity share bounded rotated-input conversion. Unity's saved SyncTestScene uses a preplaced Canvas with Inspector-wired buttons and local server X/Y/Z. The Game view renders green local and cyan remote capsules at snapshot positions over a grid, with a third-person camera following the local player. Mouse X/Y orbits the camera and WASD uses camera yaw. Scene-view Gizmos still show exact positions and IDs. Departures remove visuals. Movement remains server-authoritative and uses no physics, interpolation or prediction.
- Integration boundary - The original Unity project's opening/package errors were repaired and its previous movement screen was Play-mode verified. The new SyncTestScene revision is prepared under integrations/Unity, but remains unapplied to the original sibling project because it is outside the writable workspace. The installer backs up old auto-start scripts, adds the installed Unity 6 uGUI 2.0.0 package, and preserves scene customizations on reinstall. No review acceptance is inferred.
- Scene verification - Unity API compile check passed with zero warnings/errors, including editor Gizmo code. Windows PowerShell 5.1 installer regression passed. In a disposable project opened with actual Unity 6000.3.11f1, scene import verified Canvas references, fonts and button wiring. Play-mode checks passed local coordinates, telemetry, remote departure and disconnect reset using synthetic snapshots; local/remote Gizmos and the Canvas were visually confirmed. Corrected Unity 6 LegacyRuntime font references to file ID 10102 after the initial import exposed invisible text. Live TCP movement, focus behavior, two-client synchronization and standalone builds remain unverified.
- Verification - In-memory snapshot checks cover exact bytes, split/combined packets, maximum capacity, malformed state, room isolation, equal snapshots, departure, movement ordering, cadence and stalled-output coalescing. TCP equality assertions are added but environment connection denial still prevents execution. Added regression checks for rotated client input and Windows PowerShell 5.1 manifest repair/reinstallation. See today's development log for final build/test results. Follow [Testing.md](Testing.md) for the VS Code, Console and Unity manual review steps and expected results.


## Player gravity and floor collision (issue #2)

Review each unit before committing it and starting the next unit.

1. Gravity integration and server configuration — approved and committed. Configure the downward acceleration magnitude with `SERVER_GRAVITY_METERS_PER_SECOND_SQUARED` (default 9.81, finite and nonnegative). Store vertical velocity per player and advance height with the updated velocity.
2. Generic box and finite horizontal plane colliders — approved and committed. Both carry their own world center and dimensions and expose world bounds through Collider. Mixed-list tests cover independent placement and movement. Player dimensions and placement belong to the integration step.
3. Per-world collider list and origin floor — implemented, awaiting review. Each world creates one 10 m × 10 m plane at Y = 0. Room creation tests verify bounds and independent list and floor instances.
4. Resolve falling box contact at the plane crossing, including partial overlap, fast falls and edge departure.
5. Integrate movement and gravity for every active player between lifecycle processing and snapshot publication. Verify spawning, idle fall, stable landing and authoritative snapshot height.

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
