# Local backend and world stream

The backend composes the existing perception pipeline and publishes immutable world-perception snapshots. It is a loopback development host with a versioned read-only WebSocket endpoint. It does not execute generated policies or start a Unity scene.

## Start and configure

From the repository root:

```powershell
dotnet run --project src/OmniTwin.Backend -c Release -- --OmniTwin:EnablePerception=false
```

The default Kestrel listener is `127.0.0.1:5108`. Only loopback clients and Host values `127.0.0.1`/`localhost` are accepted. A browser WebSocket Origin, when supplied, must match the request's own HTTP origin. Native clients can omit Origin. This origin policy is a local browser boundary, not an authentication scheme for a public deployment.

Settings are read from ASP.NET configuration section `OmniTwin`. Environment keys use the usual double underscore, for example `OmniTwin__Port`; command-line keys such as `--OmniTwin:Port=5109` also work. `.env` files are not loaded automatically. Invalid configured values fail explicitly.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Port` | 5108 | Loopback HTTP port; zero selects an ephemeral port |
| `EnablePerception` | false | Explicitly enables camera and Meta calls |
| `CameraDevice` | 0 | Local camera index |
| `CalibrationPath` | unset | Required measured calibration file when enabled |
| `Concept` | cup | One SAM noun phrase |
| `Hazard` | false | Operator designation for the queried concept |
| `MaximumClients` | 16 | Concurrent WebSocket slots, configurable from 1 through 128 |
| `SendTimeoutMilliseconds` | 2000 | Deadline for one socket send |
| `MaximumPublicationAgeMilliseconds` | 2000 | Maximum age for cached/replayed publications |
| `RetentionMilliseconds` | 3000 | Observation retention, shared with tracking and wire metadata |

Before enabling perception, set `META_API_KEY` and configure a measured calibration:

```powershell
$env:OmniTwin__CalibrationPath = (Resolve-Path .\calibration.json).Path
$env:OmniTwin__EnablePerception = "true"
dotnet run --project src/OmniTwin.Backend -c Release
```

`CameraPerceptionSource` loads the profile, creates the environment-backed SAM client, requests the calibrated camera resolution, validates delivered dimensions, and runs the existing segmentation/tracking workers. No separate HTTP endpoint changes camera state. With perception disabled, the host never opens the device or calls the model.

## HTTP endpoints

| Endpoint | Behavior |
| --- | --- |
| `GET /` | Service/protocol endpoint information |
| `GET /health/live` | HTTP 200 while the host serves requests |
| `GET /health/ready` | HTTP 200 only for a running source with recent observations and no active failure/rejections; otherwise 503 |
| `GET /api/status` | Operational mode, sanitized reason, subscriber count, cache/source age, and effective currentness |
| `GET /api/world/latest` | Recent encoded wire message, or 503 when absent, too old, or terminated |
| `/ws/twin` | WebSocket upgrade requiring subprotocol `omnitwin.v1` |

Liveness is independent of camera/model readiness. A recently published snapshot may report expired or absent source observations; HTTP 200 from latest-world establishes publication availability, not scene freshness. Read the source metadata and diagnostics. Responses use `Cache-Control: no-store`.

Fatal source failure sets state `faulted`, clears the cached snapshot, and completes subscriber streams with a server-error close. Normal source completion marks `stopped`. Per-call API failures are already represented by the perception worker and do not restart the capture session or refresh old observations. Restart the process after correcting an unrecoverable camera/calibration/configuration failure. Native driver reads retain the [capture cancellation limitation](perception.md).

## Backpressure and connection ownership

`SnapshotBroker` serializes each accepted publication once. Each connection has a capacity-one channel using DropOldest. Publishing uses `TryWrite` and never waits for socket sends. A late subscriber receives the cached packet only if it remains recent. Old queued packets are skipped. Gaps in publication sequence are therefore expected.

The endpoint owns one sender and one receiver per socket, retains the request pipeline for the connection lifetime, and limits each send. Any client application data closes the read-only stream with code 1008. Normal client close is acknowledged; host/source termination ends the connection. A slot is always released on disconnect. Subscriber overflow returns HTTP 429 with `Retry-After: 1`; unsupported/missing subprotocol returns 400, and a plain HTTP request to the stream returns 426.

This lifetime model follows the asynchronous WebSocket ownership requirements described by [ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-8.0) and the single-send/single-receive constraint in [ClientWebSocket](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocket?view=net-8.0).

## Wire protocol v1

One UTF-8 JSON text WebSocket message carries one `WorldStreamMessage`. Compression is not enabled. Every key is required, including explicit nulls; all names are case-sensitive camel case. Unknown/duplicate fields, invalid versions, nonfinite values, and inconsistent metadata are rejected before a DTO is returned.

| Root field | Meaning |
| --- | --- |
| `protocolVersion` | Exactly 1 |
| `messageType` | Exactly `world.perception` |
| `streamId` | Backend-run GUID; changes after restart |
| `sequenceNumber` | Increasing delivery sequence; gaps permitted |
| `sentAtUnixMs` | Host wall-clock time when encoded |
| `retentionMilliseconds` | Observation lifetime, from 1 through 60000 ms |
| `world` | Version-one `ObstacleTrackingSnapshot` with session/calibration/source freshness and tracks |
| `failureCategory` | Sanitized category or explicit null |
| `rejectedDetectionCount` | Count from 0 through 128 |

The codec accepts at most 256 KiB and 128 tracks, with JSON depth limited to 16. It validates every nested object, vector, scalar, list ID, and constructor invariant. Tracks must be younger than retention and at least as old as the latest accepted source capture. The source currentness flag must agree with source age and transmitted retention.

`WorldStreamOrderGuard` requires increasing delivery/world sequences. The source sequence cannot decrease; the age of an unchanged source cannot decrease. Calibration stays fixed within one tracking session. A new session can reset world sequence, but returning to a retired session fails. At most sixteen session IDs are retained per connection, bounding the replay guard's memory. Restart the connection to establish a new guard context.

## Portable receiver and freshness

`OmniTwin.Transport` builds for .NET 10 and .NET Standard 2.1. `WorldStreamReceiver` consumes an already connected `WebSocket` negotiated with `omnitwin.v1`; the caller owns connection setup/disposal. It allows one active enumeration per receiver. It reassembles at most 128 receive chunks, rejects binary payloads and oversized messages, and imposes a ten-second whole-message deadline including idle time. Protocol failure ends the enumeration; invalid data is never yielded.

Use `WorldStreamFreshness.IsObservationCurrent` with locally recorded receive wall/monotonic times. It accounts for source age, delivery age, time spent in the client's cache, two-second default silence, failure/rejection metadata, and allowed clock skew. The default future clock allowance is 500 ms. Clock synchronization assumptions must hold for meaningful wall-clock delivery checks.

A consumer must clear its connected/current state when enumeration ends or fails. Keep network reading off Unity's render/physics path and apply validated values on the main thread through a bounded latest-state handoff. The .NET Standard target uses `System.Text.Json` 10.0.14 and transitive packages; Unity package/import/AOT verification remains a subsequent step. There is no claim that copying a single transport DLL creates a working Unity client.

`WorldStreamClient` now owns bounded connection/reconnection attempts and the atomic latest-state handoff. Its view reevaluates source and per-track expiry on every read; disconnected states contain no cached world. The desktop connector and real host are covered by loopback tests. [Client lifecycle, retry classification, Unity import, and scene wiring](unity-client.md).

## Probe a configured stream

In a second terminal while the enabled backend is publishing:

```powershell
dotnet run --project src/OmniTwin.Perception.Cli -c Release -- stream-probe ws://127.0.0.1:5108/ws/twin 3
```

The probe validates each packet, prints three messages, and closes normally. It has three connection attempts, five seconds per attempt, exponential retry delay, applicable `Retry-After` handling, and a thirty-second total deadline. Permanent handshake errors are not retried. A disabled host sends no invented snapshots, so the receiver eventually reports an idle timeout. The probe establishes one stream connection; reconnect after a stream error with a fresh receiver/order context.

## Evidence and limits

Tests exercise real Kestrel sockets, the actual receiver/probe, graceful/error close, source failure after a valid publication, origin/subprotocol policy, capacity recovery, bounded fanout, and blocked-send cancellation. The backend was also launched as a separate process with perception disabled and its health/status endpoints checked. See [verification results](verification.md).

The host has no public deployment/authentication configuration, agent telemetry ingestion, mission endpoint, generated-policy runner, in this stage. The separate Unity tactical scene is now wired and exercised in Play mode. Camera/API/Unity end-to-end accuracy and sustained 30 Hz performance remain unmeasured. Test source publications are explicitly confined to the test project.
