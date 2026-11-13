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
| Success | 0 | Admitted, or already in the requested room |
| SessionUnavailable | 1 | Session is not registered or is disconnected |
| RoomNotFound | 2 | Positive room ID does not identify an existing room |
| AlreadyInAnotherRoom | 3 | Admission refused; existing membership is retained |

The result code conveys success or failure; there is no separate boolean or free-text reason.
SessionUnavailable does not guarantee that an already closed connection can receive a response.
These explicit byte values are shared by server and client; do not renumber them.

Room 1 request: `00 04 03 00 00 00 01`.
Room 1 success: `00 05 04 00 00 00 01 00`.
Room 99 not found: `00 05 04 00 00 00 63 02`.

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
is resolved through that disconnect cleanup. Output queue limits remain deferred to the planned
bounded-output work; this checkpoint does not provide slow-client backpressure.

## Lifecycle

Core continuously reads the socket. EOF completes `Disconnected`; reset and receive errors populate `DisconnectError`. Pending Echo and room requests fail on closure. The console waits for input and disconnection concurrently. No application shutdown packet is defined. Silent network loss while idle requires a future heartbeat policy.

One request may be outstanding. The console applies five-second connect/request timeouts. Cancellation and I/O errors close the connection. Duplicate Connect, requests before connection, and overlapping requests are rejected. Echo content, response type, and the requested room ID are checked in Core. Malformed or mismatched responses close the connection. Valid admission failures return JoinRoomResponse normally and keep the connection usable. Invalid local room IDs fail validation without sending or closing the connection. The client does not cache room membership; a successful response describes admission when the server processed the request.

## Run

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
