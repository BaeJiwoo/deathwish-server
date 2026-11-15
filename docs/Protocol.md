# TCP packet protocol

The server and MockClient share `DeathWish.Protocol` (.NET Standard 2.1). This replaces the temporary LF-delimited Echo protocol. Update both endpoints together; old clients are incompatible.

## Frame layout

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 2 bytes | Unsigned payload length, big-endian; excludes the entire header |
| 2 | 1 byte | Message type |
| 3 | N bytes | Payload |

Maximum payload length is 4096 bytes. A complete frame is 3 + N bytes. Zero-length payloads are valid. Header and payload may arrive across multiple reads; one read may contain multiple frames. Each connection owns a PacketReader. Completed payload arrays belong to the recipient.

## Message types

| ID | Message | Direction | Payload |
| --- | --- | --- | --- |
| 1 | EchoRequest | Client to server | Strict UTF-8 text, no BOM added |
| 2 | EchoResponse | Server to client | Same text as request |
| 3 | JoinRoomRequest | Client to server | RoomId: signed int32, big-endian |
| 4 | JoinRoomResponse | Server to client | Requested RoomId: signed int32, big-endian; result: byte |
| 5 | JoinAck | Server to client | RoomId, PlayerId, ServerTick, spawn X/Y/Z |

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
Zero-length payloads are valid for Echo only. Requests and responses use TCP ordering;
the client API allows one outstanding request at a time across both Echo and room admission.

The server processes complete requests in arrival order, including mixed Echo and room requests.
Admission failures keep the connection open and preserve existing membership. Responses contain
the requested room ID even when admission fails. Sending an EchoResponse or JoinRoomResponse to
the server is a direction error and closes the connection. Failed response writes also close the
connection; ChannelInactive removes room membership before unregistering the session.
Admission does not depend on acknowledgement delivery, so failure to deliver a successful response
is resolved through that disconnect cleanup. All responses use the bounded output FIFO described below. This replaces the planned
unbounded response path.

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
than environment options. The complete ordering and shutdown contract is in [WorldAdmissionDesign.md](WorldAdmissionDesign.md).
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

JoinAck (type 5) has exactly 32 payload bytes, 35 including the header. All integers and IEEE-754
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

Positions use world units (one unit = one meter), +X right, +Y up, +Z forward. Initial spawn is
(0, 0, 0). There is no quantization yet. Example for room 1, player 2, tick 3, position (1, -2, 0.5):
`00 20 05 00 00 00 01 00 00 00 00 00 00 00 02 00 00 00 00 00 00 00 03 3F 80 00 00 C0 00 00 00 3F 00 00 00`.

The first successful JoinRoomResponse is queued before JoinAck, even if the world ticks concurrently.
Only creation emits JoinAck; repeated same-room joins return another JoinRoomResponse but no new JoinAck.
Other reliable responses retain FIFO enqueue order and may appear between acceptance and completion.
Departure before creation can suppress JoinAck. Disconnect after creation may prevent delivery;
normal departure cleanup removes the player. A received JoinAck reports admission at its tick, not ongoing connectivity.
No snapshot or broadcast is sent. Old clients reject type 5; deploy the updated protocol and client together.

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
