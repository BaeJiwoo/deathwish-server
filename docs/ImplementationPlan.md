# Implementation Plan

This plan covers the C# game server. Unity development takes place in a separate repository. Use TCP test clients to verify the server's protocol and lifecycle behavior.

Follow the review workflow in [AGENTS.md](../AGENTS.md). Each checkbox represents implementation, verification, and user review acceptance.

Stages 1 through 5 deliver connection, world admission, an admission response, and departure cleanup. Later stages add movement, snapshots, and quantization. The initial rates of 30 Hz and 10 Hz are configurable starting values.

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

- [ ] Implementation and review complete.
- Add WorldLoop and GameWorld.Tick with an initial rate of 30 Hz.
- Use monotonic elapsed time and a fixed simulation delta.
- Bound catch-up work and record tick overruns.
- Review: Ticks never overlap; cancellation stops the loop cleanly.

## Stage 4 — World admission and removal

- [ ] Implementation and review complete.
- Add WorldCommand and PlayerState; only the world loop mutates player state.
- Queue Join and Leave in connection order and assign PlayerId on admission.
- Bound admission and per-tick command processing; guarantee departure cleanup.
- Define queue overflow behavior so a full admission queue cannot discard departure cleanup. Ignore commands for departed sessions.
- Review: Immediate disconnect, duplicate Leave, and shutdown leave no players behind.

## Stage 5 — Binary admission response

- [ ] Implementation and review complete.
- Create `docs/Protocol.md` with frame length semantics, message IDs, byte order, maximum frame size, field layouts, and malformed-packet behavior.
- Use DotNetty length framing and PacketCodec for JoinAck payloads.
- Return PlayerId, ServerTick, and spawn position after world admission.
- Send through ClientSession without awaiting socket I/O in the world loop.
- Define buffer ownership and handle failed writes and slow connections.
- Keep reliable responses ordered and bound pending output. Disconnect a session when it exceeds the defined output limit.
- Review: A TCP test client decodes the exact documented bytes.
- Review: Split frames, combined frames, and invalid lengths are handled correctly.

## Stage 6 — Server-authoritative movement

- [ ] Implementation and review complete.
- Add MoveInput with an input sequence and movement direction.
- Document movement speed, coordinate axes, direction normalization, sequence comparison including wraparound, and the stale-input timeout in `docs/Protocol.md`.
- Validate finite values and direction bounds; associate input with its session.
- Keep the latest movement state per player and stop stale input after a timeout.
- Review: Packet frequency does not change movement speed; stale input stops movement.

## Stage 7 — World snapshots

- [ ] Implementation and review complete.
- Add WorldSnapshot with ServerTick and player states; start at 10 Hz.
- Specify full-snapshot semantics for player creation and removal on the client.
- Document snapshot field layouts and ordering after JoinAck in `docs/Protocol.md`.
- Bound pending sends and coalesce snapshots that have not started transmission.
- Review: Two TCP test clients decode matching membership and positions for the same server tick without blocking ticks.

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
