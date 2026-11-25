# TCP packet protocol

The server and MockClient share `DeathWish.Protocol` (.NET Standard 2.1). This replaces the temporary LF-delimited Echo protocol. Update both endpoints together; old clients are incompatible.

## Frame layout

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 2 bytes | Unsigned payload length, big-endian; excludes the entire header |
| 2 | 1 byte | Message type |
| 3 | N bytes | Payload |

Maximum payload length is 4096 bytes for messages other than WorldSnapshot. WorldSnapshot (7) permits up to 20,494 bytes for 1,024 players. This exception applies only to server-to-client snapshots. The inbound server length decoder remains limited to 4,099-byte frames. A complete frame is 3 + N bytes. Header and payload may arrive across multiple reads. One read may contain multiple frames. Each connection owns a PacketReader. Completed payload arrays belong to the recipient.

## Message types

| ID | Message | Direction | Payload |
| --- | --- | --- | --- |
| 1 | EchoRequest | Client to server | Strict UTF-8 text, no BOM added |
| 2 | EchoResponse | Server to client | Same text as request |
| 3 | JoinRoomRequest | Client to server | RoomId: signed int32, big-endian |
| 4 | JoinRoomResponse | Server to client | Requested RoomId: signed int32, big-endian; result: byte |
| 5 | JoinAck | Server to client | RoomId, PlayerId, ServerTick, spawn X/Y/Z, optional map ID |
| 6 | MoveInput | Client to server | Sequence: uint32; direction X/Z: float32 |
| 7 | WorldSnapshot | Server to client | RoomId, ServerTick, player count, complete player states |
| 8 | Jump | Client to server | Empty |
| 9 | CameraRay | Client to server | Six big-endian float32 values. Camera offset XYZ in meters from the player's feet, then unit direction XYZ. Both use world axes. |
| 10 | RaycastResult | Server to requesting client | UTF-8 hit kind, optional player ID, world point and distance in meters, or a miss |
| 11 | JoinMatchRequest | Client to server | RoomId int32, optional capability marker and flags, then the UTF-8 nickname |
| 12 | MatchState | Server to client | Full match roster and state |
| 13 | KillEvent | Server to client | One confirmed kill |
| 14 | LatencyProbe | Server to client | Positive opaque int64 token |
| 15 | LatencyReply | Client to server | The exact int64 token from the probe |
| 16 | ArenaShot | Server to opted-in arena clients | Accepted firing event with authoritative ray endpoints and outcome |

The server processes at most one queued action per player per fixed tick. A grounded jump targets a 1.8 m rise under positive gravity. The server casts up to 100 m from the authoritative foot position plus the validated camera offset and excludes the requesting player's body. The offset magnitude is at most 8 m. The direction length must be between 0.99 and 1.01 before the server normalizes it. CameraRay doubles as the shot input in TeamDeathmatch. Flat and PhysicsLab return diagnostic results.

Example request for `Hi`: `00 02 01 48 69`. Response: `00 02 02 48 69`.
LF, CR, and NUL are valid payload characters. Unknown types, incorrect direction, invalid UTF-8, or oversized lengths close the connection. Oversized lengths are rejected after reading the header, before allocating the body. Incomplete frames at disconnect are discarded; the client reports truncated responses as errors.

## Room join protocol

The shared codec and server handler support room joins. The server associates each request with
the receiving handler's session, calls RoomManager.Join, and responds on the same connection.
MockClient exposes `JoinRoomAsync(int roomId, CancellationToken)` and the `/join <roomId>` console command.
Existing Echo frames are unchanged. Older endpoints do not support message IDs 3 and 4.

| Message | Payload offset | Size | Field |
| --- | --- | --- | --- |
| JoinRoomRequest | 0 | 4 bytes | RoomId |
| JoinRoomResponse | 0 | 4 bytes | Requested RoomId, including on failure |
| JoinRoomResponse | 4 | 1 byte | RoomJoinResult |

Request payload length is exactly 4 (7-byte frame); response length is exactly 5 (8-byte frame).
RoomId ranges from 1 to 2147483647. It names the requested room, not the session's current room.
The request contains no SessionId; the server will identify the sender from its connection.

| Result | Value | Meaning |
| --- | --- | --- |
| Success | 0 | Room membership and world admission reservation accepted, or already in the requested room |
| SessionUnavailable | 1 | Session is not registered or is disconnected |
| RoomNotFound | 2 | Positive room ID does not identify an existing room |
| AlreadyInAnotherRoom | 3 | Admission refused; existing membership is retained |
| WorldUnavailable | 4 | World admission capacity is exhausted, or server/world shutdown has begun |
| NicknameRequired | 5 | The client attempted to join TeamDeathmatch using an unnamed room request |

The result code conveys success or failure; there is no separate boolean or free-text reason.
SessionUnavailable does not guarantee that an already closed connection can receive a response.
These explicit byte values are shared by server and client; do not renumber them.
Value 4 is added in Stage 4 without changing frame layouts or values 0 through 3. Older codecs
reject this new result as unknown; rebuild/deploy the shared protocol with both endpoints.
WorldUnavailable leaves the connection open and creates no room membership or player for a new
request. The client may retry after capacity becomes available; no retry interval is specified.

Success acknowledges acceptance, not completion of a world update. The room reserves capacity
and queues Join before responding. The world assigns PlayerId when a later tick processes Join.
Repeated same-room requests do not reserve another slot or enqueue another Join. A disconnect
before processing prevents admission or queues its cleanup; it does not require an acknowledgement.
After player creation, Stage 5 sends JoinAck as specified below.

Room 1 request: `00 04 03 00 00 00 01`.
Room 1 success: `00 05 04 00 00 00 01 00`.
Room 99 not found: `00 05 04 00 00 00 63 02`.
Room 1 world unavailable: `00 05 04 00 00 00 01 04`.

Encoders return complete frames; decoders accept payloads only. Both validate room IDs and result codes.
Wrong payload lengths, nonpositive room IDs, and unknown result bytes throw InvalidDataException;
null payload arrays throw ArgumentNullException. PacketReader checks framing and known message IDs;
the message-specific codec checks payload contents and the endpoint checks direction.
Malformed room messages close the connection, while valid admission
failures return JoinRoomResponse. A positive nonexistent room ID is a valid request.
Zero-length payloads are valid for Echo and Jump. Requests and responses use TCP ordering;
the client API allows one outstanding request at a time across both Echo and room admission.

The server processes complete requests in arrival order, including mixed Echo and room requests.
Admission failures keep the connection open and preserve existing membership. Responses contain
the requested room ID even when admission fails. Sending an EchoResponse or JoinRoomResponse to
the server is a direction error and closes the connection. Failed response writes also close the
connection; ChannelInactive removes room membership before unregistering the session.
Admission does not depend on acknowledgement delivery, so failure to deliver a successful response
is resolved through that disconnect cleanup. All responses use the bounded output FIFO described below. This replaces the planned
unbounded response path.

## TeamDeathmatch protocol

`JoinMatchRequest` uses a positive int32 room ID at offset 0 and the nickname through the end of its payload. The nickname begins at offset 4 for the original request. To receive `ArenaShot` events, add bytes `00 01` at offsets 4 and 5 and begin the nickname at offset 6. The zero marker is distinguishable from a valid nickname because NUL is forbidden in names. Flag byte `01` is the only valid extension value. The first successful room join fixes the capability for the admission. The original request remains supported and receives no `ArenaShot` events. The request is followed by a normal `JoinRoomResponse` and, on admission, `JoinAck`. The client trims the nickname before encoding it. The wire name is 1 to 16 Unicode scalar values, up to 64 UTF-8 bytes, and must have no surrounding whitespace. Controls, format characters, line and paragraph separators, and angle brackets are rejected. Players are identified by PlayerId. Multiple players can choose the same nickname.

TeamDeathmatch sends `MatchState` and `KillEvent` after its `JoinAck`. Flat and PhysicsLab keep their original position-only world snapshots. The normal world snapshot still supplies TeamDeathmatch positions. A player with zero health stays in the match roster while their body is unavailable. The receiver uses increasing server ticks for match state and applies each kill event once. Several kills can share a tick.

| MatchState offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | RoomId, int32 |
| 4 | 8 | ServerTick, int64 |
| 12 | 1 | MatchPhase, 0 Waiting, 1 Playing, 2 Finished |
| 13 | 1 | Winner, 0 absent, 1 Red, 2 Blue |
| 14 | 4 | RedScore, int32 |
| 18 | 4 | BlueScore, int32 |
| 22 | 1 | PlayerCount, at most 10 |
| 23 | variable | Player records |

Each player record contains PlayerId (int64), team (byte, 1 Red or 2 Blue), health (byte, 0–100), kills, deaths, assists, and ping milliseconds (four int32 values), followed by a nickname. Ping uses -1 before a measurement is available. Each record nickname is prefixed with its UTF-8 byte length in one byte. Team capacity is five players.

`KillEvent` consists of RoomId (int32), ServerTick (int64), attacker PlayerId (int64) and victim PlayerId (int64), followed by the attacker and victim nicknames, each with the same one-byte length prefix. All integers use big-endian byte order. The feed receives the names stored at the time of the kill.

| ArenaShot offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | RoomId, positive int32 |
| 4 | 8 | ServerTick, positive int64 |
| 12 | 8 | ShooterPlayerId, positive int64 |
| 20 | 12 | Origin XYZ in meters, three finite float32 values |
| 32 | 12 | End XYZ in meters, three finite float32 values |
| 44 | 1 | Outcome, 0 Miss, 1 Terrain, 2 PlayerContact, 3 Damage |

All fields use the simulation world axes and big-endian encoding. After `JoinAck`, each accepted shot is sent to subscribed clients in its arena, including the shooter if subscribed. Ticks never decrease and multiple shooters may share a tick. `PlayerContact` means the ray hit a player body without applying damage, such as a teammate. `Damage` confirms that the hit reduced an enemy's health. The origin is the server body's shot origin. The end is the hit point or the end of the tested ray on an ordinary miss. If terrain occludes the camera from the body origin, the event is a miss with identical origin and end so no tracer crosses the obstruction. Cooldown and inactive player or match rejections yield the existing `RaycastResult` miss and no firing event. Overflow of the server's pending action queue remains a connection error. Subscribed clients must still drain the original `RaycastResult` response stream. Core holds the latest 64 shot events in receive order and discards the oldest when full.

Core exposes the opt-in `JoinMatchAsync(int roomId, string nickname, bool receiveArenaShots, CancellationToken)` overload and `TryReadArenaShot`. Use the original overload to join without the extension. A client that requests the extension requires a server supporting it.

TeamDeathmatch begins in Waiting and moves to Playing when both teams fill. A confirmed enemy kill adds one team point. The game transitions to Finished when a team reaches 10 points and publishes its winner. The other active attackers who damaged the victim within the preceding five simulation seconds receive one assist. A departure frees the slot and retains scores. During play, a replacement joins the smaller team. After a finish, new admission waits for the room to empty and reset.

The training room also uses the TeamDeathmatch map ID and packet layouts. It enters Playing on its first active admission and has five slots per team. Health, death, the three-second respawn and stats follow the same rules. A training room keeps playing when a team reaches 10 points and omits a winner. The MockClient's local patrol controller sends normal movement and jump frames and observes roster health, deaths and position snapshots to recover from respawns.

After admission and the initial match state, the server sends an eight-byte `LatencyProbe`. The client automatically responds with the same token in `LatencyReply`. Each admission has at most one outstanding token. The server sends probes at least two seconds apart and accepts the matching reply within five seconds of the send. Using its monotonic clock, it rounds the measured RTT to integer milliseconds and includes it in later match state. Unmatched or expired replies cannot set a latency value. A missing measurement is -1.

## Lifecycle

Core continuously reads the socket. EOF completes `Disconnected`; reset and receive errors populate `DisconnectError`. Pending Echo and room requests fail on closure. The console waits for input and disconnection concurrently. No application shutdown packet is defined. Silent network loss while idle requires a future heartbeat policy.

One request may be outstanding. The console applies five-second connect/request timeouts. Cancellation and I/O errors close the connection. Duplicate Connect, requests before connection, and overlapping requests are rejected. Echo content, response type, and the requested room ID are checked in Core. Malformed or mismatched responses close the connection. Valid admission failures return JoinRoomResponse normally and keep the connection usable. Invalid local room IDs fail validation without sending or closing the connection. The client caches the accepted room and first JoinAck; a successful response describes admission when the server processed the request.

## Run

Run from the repository root. ServerOptionsLoader reads `.env` from the current working
directory and creates immutable ServerOptions before any networking resources are allocated.
Configuration precedence is CLI address/port > process environment > `.env` > code defaults.
Room count and tick rate are not CLI arguments. Missing `.env` files are allowed; malformed files, unreadable
files, and invalid effective settings stop startup with a configuration error and exit code 1.
Only effective values are validated after precedence resolution; file syntax is always checked.

Copy `.env.example` to `.env` for a fresh checkout:

```powershell
Copy-Item .env.example .env
```

```dotenv
SERVER_ADDRESS=127.0.0.1
SERVER_PORT=7777
SERVER_ROOM_COUNT=2
SERVER_TICK_RATE=30
```

Change SERVER_ROOM_COUNT to 10 and restart to create rooms 1 through 10 without rebuilding.
Set optional `SERVER_TDM_ROOM_ID` to an integer from 1 through `SERVER_ROOM_COUNT` to select the team deathmatch arena. `SERVER_TDM_TEAM_SIZE` is the number of slots per team, from 1 to 5, with a default of 5. Set it to 1 for a local 1v1 match. Optional `SERVER_TRAINING_ROOM_ID` selects a separate practice room in the same range. The two room IDs must be distinct. The highest room defaults to PhysicsLab when neither arena is configured on it. Other rooms use the flat map.
Change SERVER_TICK_RATE to 60 and restart to target 60 world updates per second in every room.
Address must be an IP literal, port must be 1 through 65535, and room count and tick rate must be
positive int32 values. Code defaults are 127.0.0.1, 7777, 2 rooms, and 30 Hz. Options are read only
at startup. Actual timing depends on scheduler resolution and workload; a positive tick rate is
not a performance guarantee. The loop runs at most four catch-up ticks before discarding excess
backlog. Start and shutdown logs report the configured rate and loop statistics respectively.
World ticks now process queued admission/departure and maintain player identity and initial
position. Each room allows at most 1024 outstanding admissions (pending, active, or waiting for
removal), and processes at most 128 world commands per tick. Each admission reserves its own
Leave command, so full admission capacity cannot drop cleanup. These are code defaults rather
than environment options. TDM admission is capped by twice its configured team size and uses the authored spawn slots. The complete ordering and shutdown contract is in [WorldAdmissionDesign.md](WorldAdmissionDesign.md).
The minimal `.env` reader supports KEY=VALUE, blank lines, full-line # comments, whitespace,
single/double-quoted literal values, and trailing # comments (unquoted comments require preceding
whitespace). Duplicate keys use the last value. Keys are case-sensitive in the file; unrelated
valid keys are ignored. No variable expansion, escape processing, multiline values, or export
prefix is supported. Loading the file does not change the process environment or print its values.
`.env` and `.env.*` are ignored by Git except `.env.example`.
This does not add dynamic room creation, match pooling, or reset logic.

Run in separate terminals:

```powershell
dotnet run --project src/DeathWish.Server -- 127.0.0.1 7777
dotnet run --project mockclient/DeathWish.MockClient.Console -- 127.0.0.1 7777
```

Enter messages and check `Echo PASS` and server logs. `/join 1` joins room 1 and prints `Join room 1: Success`. Repeating it returns success without duplicate membership. `/join 2` then reports `AlreadyInAnotherRoom`; `/join 99` reports `RoomNotFound`. Invalid syntax or nonpositive IDs print usage without sending a request. Interactive admission failures keep the prompt available. `/quit` exits.

Append a quoted message or command argument for a single request:

```powershell
dotnet run --project mockclient/DeathWish.MockClient.Console -- 127.0.0.1 7777 "/join 1"
```

Exit code is 0 on success/user cancellation and 1 on transport failure. In single-request mode, invalid command syntax or rejected admission also returns 1. After handling interactive admission failures, a normal `/quit` returns 0.

Unity integration uses both MockClient.Core and DeathWish.Protocol; target Unity API compatibility must support .NET Standard 2.1. Actual Unity integration remains unverified.

Regression checks:

```powershell
dotnet run --project tests/DeathWish.ConnectionTests
```

Exit code 2 indicates socket checks were skipped due to environment permissions. In-memory checks execute first.

## World admission completion (Stage 5)

JoinAck (type 5) has a 32-byte base payload. A flat room sends only the base payload. PhysicsLab appends map ID 1 and TeamDeathmatch appends map ID 2 at offset 32. All other appended values and payload lengths are invalid. Use the updated shared codec to join TeamDeathmatch. All integers and IEEE-754
binary32 position bits use big-endian byte order. Invalid lengths, nonpositive IDs/ticks, and
nonfinite coordinates are protocol errors. RoomId is positive int32; PlayerId and ServerTick are positive int64.

| Payload offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | RoomId |
| 4 | 8 | PlayerId, unique within this world run |
| 12 | 8 | ServerTick in which the player was created |
| 20 | 4 | Spawn X, float32 |
| 24 | 4 | Spawn Y, float32 |
| 28 | 4 | Spawn Z, float32 |
| 32 | 0 or 1 | Map ID. Omit for flat, 1 for PhysicsLab, 2 for TeamDeathmatch. |

Positions use world units (one unit = one meter), +X right, +Y up, +Z forward. The server sends the selected spawn's foot position in JoinAck. TeamDeathmatch uses the spawn markers from its shared JSON. There is no quantization yet. Example for room 1, player 2, tick 3, position (1, -2, 0.5):
`00 20 05 00 00 00 01 00 00 00 00 00 00 00 02 00 00 00 00 00 00 00 03 3F 80 00 00 C0 00 00 00 3F 00 00 00`.

The first successful JoinRoomResponse is queued before JoinAck, even if the world ticks concurrently.
Only creation emits JoinAck; repeated same-room joins return another JoinRoomResponse but no new JoinAck.
Other reliable responses retain FIFO enqueue order and may appear between acceptance and completion.
Departure before creation can suppress JoinAck. Disconnect after creation may prevent delivery;
normal departure cleanup removes the player. A received JoinAck reports admission at its tick, not ongoing connectivity.
World snapshots now follow admission as described in Stage 7 below. Old clients reject types 5 and 7; deploy the updated protocol and client together.

ClientSession owns a FIFO limited to 64 frames and 262144 bytes, including its one in-flight write.
Overflow rejects the new frame, discards queued frames, and closes the connection. Each submitted write
has a five-second deadline; failure or timeout also closes the connection. These are code defaults.
The world never awaits socket completion. Queued managed arrays belong to the session; after wrapping and
submission, DotNetty owns the reference-counted buffer, including failure/timeout cleanup. No reliable
frame is coalesced. A short output lock orders admission acceptance with world notifications; socket
completion is never awaited under it. The production pipeline uses LengthFieldBasedFrameDecoder with
maximum length 4099, offset 0, field width 2, adjustment 1, and strip count 0, followed by PacketReader.

MockClient receives JoinAck independently of request responses and caches the first admission.
After successful JoinRoomAsync, call WaitForAdmissionAsync with a cancellation token. Duplicate,
unsolicited, wrong-room, or malformed JoinAck closes the connection. Cancellation while waiting closes
 the connection; disconnect fails an incomplete admission wait. The console's /join waits for completion
and prints PlayerId, ServerTick, and spawn within the existing five-second command deadline.

## Server-authoritative movement (Stage 6)

MoveInput (type 6) has exactly 12 payload bytes, 15 including the header. The client sends input,
not position, elapsed time, speed, RoomId or PlayerId. The handler uses its own session and current
admission to identify the player. The message has no response; successful local sending does not
confirm that the world has applied the input.

| Payload offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | Sequence: uint32, big-endian |
| 4 | 4 | Direction X: IEEE-754 binary32 bits, big-endian |
| 8 | 4 | Direction Z: IEEE-754 binary32 bits, big-endian |

Example: sequence 16909060 (0x01020304), X=1, Z=-0.5:
`00 0C 06 01 02 03 04 3F 80 00 00 BF 00 00 00`.
Both components must be finite and in [-1, 1]. Zero and negative zero are valid. Invalid component
values or payload lengths close the connection and trigger departure cleanup. A valid input before
room membership, after departure, or during shutdown is silently ignored. Servers never send type 6;
a client receiving it treats it as a direction error. Existing message layouts remain unchanged.
Older servers reject type 6, so update both endpoints. Shared source constants HEADER_SIZE and
MAX_PAYLOAD_LENGTH replace their former PascalCase names; this rename does not change the wire format.

### Direction, speed and tick ordering

Movement input controls horizontal velocity (+X right, +Z forward). Y points up and one unit is one meter.
Directions longer than 1 are normalized. Shorter directions retain their analog magnitude. Maximum horizontal
speed is 5 m/s. Each occupied world processes Join/Leave, sets horizontal velocities, advances its Bepu
simulation once for the fixed tick, then publishes the players' foot positions. A zero or expired input
stops horizontal movement while gravity continues. The floor spans 100 m × 100 m at the origin. Players
can walk off its edge and fall. Gravity magnitude comes from SERVER_GRAVITY_METERS_PER_SECOND_SQUARED.

One lock-protected latest-input slot belongs to each admission token; movement does not enqueue world
commands. Newer packets replace the slot, so a burst cannot consume Join/Leave queue capacity. The world
loop alone modifies PlayerState and publishes copied states after movement. The slot's storage is bounded
by admission capacity. This is not a transport packet-rate limiter; decoding still costs work per packet.

Input may be stored while accepted admission is awaiting creation. It retains its original receipt age;
old pending input is not refreshed by spawning. JoinAck reports the spawn position before movement in the
creation tick. MockClient only sends movement after receiving JoinAck. Departure closes and clears the slot;
a departing player cannot continue moving while its Leave command waits. Rejoining allocates a new slot
and resets sequence history. World shutdown closes all slots before clearing players.

### Sequence ordering and expiration

The first valid sequence for an admission may be any uint32, including zero. A subsequent sequence is newer
only when unchecked(candidate - previous) is strictly between 0 and 0x80000000. Equal, older and exactly
half-range differences are ignored. Thus uint.MaxValue followed by 0 is valid. Clients increment modulo
2^32 and must not jump ahead by 2^31 or more. Rejected sequences do not replace direction or refresh age.

Accepted input is timestamped using the server's monotonic TimeProvider when the handler stores it.
At each movement update, age >= 250 ms produces zero movement. Sequence history remains after expiry;
a newer sequence is required to resume. Expiry uses elapsed real time, not simulation tick count, so stale
input stays stopped after a long pause or during catch-up ticks. The boundary is sampled per player at its
movement update; no fractional-tick integration is performed. Send refreshed held input regularly, for
example at 30 Hz. No client timestamp is trusted. Wall-clock date changes do not determine movement expiry.

### MockClient and verification

After JoinRoomAsync and WaitForAdmissionAsync, call SendMoveAsync(new MoveInput(sequence, x, z), token).
Await each send/request before starting another: Echo, join and movement share the single-operation guard
so stream writes cannot interleave. Local invalid input or a pre-admission call fails before writing;
cancellation or I/O failure after sending starts closes the connection. There is no MoveInput response.

For manual use, start the server and interactive Console as above, then enter:

```text
/join 1
/move 1 0
/move 0 0
```

The Console increments its sequence and sends one input per /move command. It does not continuously refresh
held input; a nonzero command expires after 250 ms without another fresh command. Invalid syntax does not
disconnect an interactive client. The /move command reports sending only;
use /wasd to refresh held input and display the authoritative position from WorldSnapshot. See Stage 7 below.


## WorldSnapshot (Stage 7)

All integer fields and IEEE 754 float32 bit patterns use big-endian byte order.

| Payload offset | Bytes | Field |
| --- | --- | --- |
| 0 | 4 | Positive int32 RoomId |
| 4 | 8 | Positive int64 ServerTick, after this tick's movement |
| 12 | 2 | uint16 PlayerCount, 0–1024 |
| 14 + 20*i | 8 | Positive int64 PlayerId |
| 22 + 20*i | 4 | float32 X in meters |
| 26 + 20*i | 4 | float32 Y in meters |
| 30 + 20*i | 4 | float32 Z in meters |

Payload length must equal `14 + 20 * PlayerCount`. Player IDs must be unique, coordinates finite; server entries are ordered by PlayerId. This is a full room snapshot: create unknown IDs, update included positions and remove absent IDs. Session IDs are never transmitted. The client's own admitted player must remain present while connected. Core rejects snapshots before JoinAck, wrong rooms, ticks before admission and non-increasing snapshot ticks. It retains only the latest immutable snapshot, accessible from the UI thread through `LatestSnapshot`.

At default 30 Hz, the world broadcasts after every third tick (10 Hz), following lifecycle processing and movement. A simulation-time accumulator supports other tick rates, emitting no more than once per tick. JoinRoomResponse precedes JoinAck, which precedes that connection's first snapshot. Both clients in one room receive the same encoded snapshot for a given tick. Slow clients may skip ticks.

Reliable responses retain their FIFO. Each session also has at most one unsent snapshot, replaced by newer snapshots; the one in-flight frame is never replaced. Reliable frames are selected first. Both queues and the in-flight frame count toward 64 frames / 256 KiB; overflow or a five-second write timeout closes the session. World ticks never await network completion. The snapshot array is shared read-only among recipients and remains immutable for the transport lifetime.

Snapshot support is a protocol extension without negotiation: update server and clients together. Older clients reject message 7. Echo's 4096-byte bound is unchanged. No quantization is applied.

WASD console and Unity setup: [movement integration guide](../integrations/Unity/README.md).


### Client input conversion and focus

Console and Unity use the shared `MovementInput.FromLocalAxes` helper. Local axes are validated, diagonal input is normalized, yaw rotation uses double precision, and final X/Z components are clamped to [-1, 1] to prevent floating-point rounding from creating invalid packets. Wire validation remains strict. Console WASD requires a standalone Windows Console Host window; the actual console HWND must be foreground, and hidden pseudoconsole hosts are rejected. The provided launcher opens a supported window. Unity sends after processing the current snapshot so an immediate send failure cannot invalidate a subsequent connection read in that frame.
