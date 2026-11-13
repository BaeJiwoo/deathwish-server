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

## Deferred design considerations

- [ ] Review resource ownership in `GameServer.RunAsync` before introducing separate lifecycle methods or broadcasting.
  - Keep `acceptGroup` and `connections` local while `RunAsync` owns their creation, use, and cleanup.
  - Reconsider making `acceptGroup` a field if `StopAsync` or `DisposeAsync` needs to manage event-loop shutdown.
  - Reconsider making `connections` a field if other methods need to broadcast or close connections.
  - Before moving either resource to a field, define initialization and cleanup ownership, access before startup and after shutdown, and concurrent or repeated `RunAsync` behavior.
