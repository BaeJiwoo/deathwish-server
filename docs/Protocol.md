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

Example request for `Hi`: `00 02 01 48 69`. Response: `00 02 02 48 69`.
LF, CR, and NUL are valid payload characters. Unknown types, incorrect direction, invalid UTF-8, or oversized lengths close the connection. Oversized lengths are rejected after reading the header, before allocating the body. Incomplete frames at disconnect are discarded; the client reports truncated responses as errors.

## Lifecycle

Core continuously reads the socket. EOF completes `Disconnected`; reset and receive errors populate `DisconnectError`. Pending Echo calls fail on closure. The console waits for input and disconnection concurrently. No application shutdown packet is defined. Silent network loss while idle requires a future heartbeat policy.

One Echo may be outstanding. The console applies five-second connect/Echo timeouts. Cancellation and I/O errors close the connection. Duplicate Connect and Echo-before-connect calls are rejected. Echo content is checked in Core for reuse in Unity.

## Run

Run in separate terminals:

```powershell
dotnet run --project src/DeathWish.Server -- 127.0.0.1 7777
dotnet run --project mockclient/DeathWish.MockClient.Console -- 127.0.0.1 7777
```

Enter messages and check `Echo PASS` and server logs. `/quit` exits. Append a quoted message argument for a single request. Exit code is 0 on success/user cancellation and 1 on failure.

Unity integration uses both MockClient.Core and DeathWish.Protocol; target Unity API compatibility must support .NET Standard 2.1. Actual Unity integration remains unverified.

Regression checks:

```powershell
dotnet run --project tests/DeathWish.ConnectionTests
```

Exit code 2 indicates socket checks were skipped due to environment permissions. In-memory checks execute first.
