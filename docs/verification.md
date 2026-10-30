# Foundation, perception, tracking, and transport verification

Verified on 2026-10-30 on Windows x64 using .NET SDK 10.0.403 with domain targets `net10.0` and `netstandard2.1`; the test hosts run on .NET 10.

## Commands

Run from the repository root:

```powershell
dotnet restore OmniTwin.sln --locked-mode
dotnet build OmniTwin.sln -c Release --no-restore
dotnet test --solution OmniTwin.sln -c Release --no-build --no-restore
```

The build completed with zero warnings and zero errors. The latest verification run passed all 291 test executions: 104 against the .NET 10 core/transport binaries, the same 104 against the .NET Standard 2.1 binaries, 46 runtime-adapter tests, and 37 backend tests. There were zero failures and zero skipped tests. Both shared suites assert the target-framework attributes of the loaded domain assemblies. Locked restore, CLI `--help`, and `calibration-check` against an actually downloaded browser profile also succeeded.

The calibration milestone added native JPEG snapshot/no-overwrite/concurrent-writer checks and real Kestrel tests for embedded assets, profile attachments, closed-schema and geometry rejection, same-origin/custom-header requirements, known/chunked body limits, and a five-second incomplete-body timeout. These checks do not open physical camera hardware.

The optional `tests/CalibrationBrowser` check ran with Playwright 1.64.1 and its isolated Chromium Headless Shell 156.0.8078.4. Twelve browser checks passed: JPEG decoding, four pointer selections, undo, actual JSON download, JSON-only submission, geometry rejection, invalid-edit download revocation, reset, two controlled 503 responses followed by real server validation, EXIF rejection, oversized JPEG header rejection, and narrow layout. No page errors were reported. The downloaded 640 × 480 profile passed the real CLI parser. Test inputs are visibly labeled synthetic, and no test detections are streamed into Unity. The repeatable command is in the [calibration guide](calibration.md).

The browser check is an optional local command; the existing hosted workflow runs the .NET checks but does not run this browser script. No production backend package requires Node.js. Physical camera capture, measured calibration accuracy, and live model access remain unverified.

The earlier reference-assembly check was followed by actual Editor integration on Unity **6000.6.3f1**, URP **17.6.0**. The user-created project imported the seven portable DLLs and adapters, and compiled the camera, UI Toolkit HUD, and scene authoring tool. The saved reference-floor NavMesh reported 16 triangulation vertices. The tactical scene entered Play mode and sampled frame counts increased. No other Unity project was modified.

Inside Unity's Mono runtime, a codec verification fixture encoded/decoded 788 UTF-8 bytes, preserved `(1.25, -2.5)`, and rejected `{}`. It was not published to the simulation. The real disabled-perception backend accepted the Unity client's WebSocket. Clicking the HUD reconnect button through the native window changed the client to `Connected`, attempt 1, with currentness false. A wheel input changed camera position. Source silence correctly exhausted the bounded receive budget. No Meta key or physical camera was used.

Final compiler checks passed. Play-mode console checks showed no application errors; the expected `receive_timeout` warning occurred while the disabled source remained silent. A real Game-view screenshot is in `docs/images/tactical-desk.png`. These are functional checks, not a sustained performance benchmark, standalone build, IL2CPP check, or validation of camera-driven agent navigation.

`./tests/UnityImport/Verify-UnityImport.ps1` passed using filesystem fixtures. The real installer selected and copied seven portable DLLs and two Unity scripts, preserved an existing `.meta` file on repeat import, rejected a local source edit without altering its ownership record, and rejected duplicate assembly names elsewhere in Assets. These fixtures are import-test inputs, not an authored Unity scene or a substitute camera source.

A separately launched backend process was also checked on an ephemeral loopback port with perception disabled: liveness returned 200, readiness/latest-world returned 503, and status reported `disabled`. That process was terminated after the smoke check.

To retain machine-readable results locally:

```powershell
dotnet test --solution OmniTwin.sln -c Release --no-build --no-restore --report-trx
```

Microsoft.Testing.Platform writes reports under the repository root's `TestResults` directory, which is excluded from version control.

The .NET 10 migration also passed `dotnet restore OmniTwin.sln --locked-mode`. The stable direct-package update query reported no newer versions; the NuGet vulnerability query including transitive dependencies reported no known vulnerable package entries on the verification date. These catalog checks are not a security audit. The SDK used for local verification was downloaded from Microsoft's release metadata into a temporary workspace directory and its archive's SHA-512 was verified before use; install the pinned SDK for ordinary `dotnet` commands on another machine.

## GitHub verification status

The source was pushed to the private [Mr444-dev/OmniTwin repository](https://github.com/Mr444-dev/OmniTwin). The [initial Verify run](https://github.com/Mr444-dev/OmniTwin/actions/runs/41088989197) failed before any runner step started. GitHub's check annotation reports an account payment or spending-limit restriction and directs the account owner to **Billing & plans**. Hosted CI has therefore not executed; the passing results above are local results. Account billing settings were not changed. After the account restriction is resolved, rerun Verify from the Actions page; the workflow also supports manual dispatch.

## Coverage of behavior

- Rectangle corners, center, interior points, and documented extrapolation.
- A known perspective transform evaluated against an independently defined rational formula over a 21-by-21 grid.
- Reversed corner order/reflected axes and a `1e12` pixel-coordinate offset.
- Duplicate, collinear, concave, self-crossing, nearly collapsed, empty, and null calibration inputs.
- Projective horizon, near-horizon, nonfinite inputs, and float world-coordinate overflow.
- Ten thousand concurrent queries and calibration-input mutation after construction.
- Snapshot defensive copies, read-only collection views, duplicate/null entries, IDs, enums, numeric ranges, text limits, schema version, and sequence metadata.
- JSON round trips of nonzero vector values inside snapshots and directives, mission values, and finite initialization checks.
- Distance calculation with double intermediates for components near float limits.
- Eighteen mask fixtures encoded and decoded by the official `meta-sam-parser` 0.1.0, compared byte-for-byte against the C# one_bit decoder on both core targets.
- Malformed/oversized masks, frame-difference rate limiting, motion settling, periodic refresh, input ownership, and monotonic-time validation.
- SAM request shape, confidence/box/mask grammar, duplicate JSON keys, duplicate response-local IDs, wrong dimensions/model/content type, incomplete responses, and unexpected lossless masks.
- Bounded retries for HTTP 429/503, permanent HTTP failures, network/body-read failures, retry exhaustion, `Retry-After` exceeding the operation budget, caller cancellation, overlapping-call rejection, per-attempt timeouts, and total deadlines.
- Response-size limits with and without a known content length, and timeout enforcement while reading the response body.
- Latest-frame coalescing during a blocked inference call, serial execution, API failure reporting, and rejection of old/future frames and results that become stale during inference.
- Native OpenCV JPEG encoding/decoding on Windows x64, defensive image ownership, dimension validation, and EXIF rotation normalization.
- Session-stable IDs despite reordered detections and changed SAM IDs; global association where greedy pairing would lose a valid match; crossing trajectories after velocity estimation.
- Hungarian assignment checked against an exhaustive independent oracle on small rectangular matrices, rather than duplicating the production algorithm in tests.
- Radius, label, and hazard association gates; atomic capacity rejection; no ID reuse after expiry; replay/capture-order rejection; confidence decay; concurrent publication sequence uniqueness.
- Monotonic aging including inference delay, exact expiry boundary, immutable tracking snapshots, JSON round trips, and explicit differentiation of an unobserved/expired scene from a fresh empty scene.
- Full-circle workspace bounds, resized inclusive mask boxes, bottom-band support, one-pixel masks, clearance/radius limits, low-confidence/empty/outside detections, and calibration-image dimension mismatch.
- Closed-schema calibration JSON and bounded/cancellable file loading, including duplicate/unknown/missing keys and invalid numeric values.
- World publication and expiry while the observation source is blocked, projection diagnostics, replay rejection, and inference failures that do not refresh observation age.
- Strict camel-case wire round trips, required/unknown/duplicate fields, unsupported protocol, inconsistent retention, invalid/oversized UTF-8 payloads, fragmentation limits, binary messages, idle deadlines, and cancellation on both transport targets.
- Server/world/source sequence monotonicity, coalescing gaps, calibration consistency, retired-session rejection, and client freshness that includes delivery age and local cache age.
- Capacity-one subscriber queues tested with an intentionally unread subscriber and an independently draining subscriber; recent-only replay, subscriber cleanup, atomic publication failure, and a socket send that blocks until its deadline.
- Real Kestrel/ClientWebSocket communication on ephemeral loopback ports, HTTP/WS agreement, fatal-source cache clearing/error close, read-only policy, foreign-origin/Host rejection, required subprotocol, subscriber limits, and host shutdown.
- The actual `stream-probe` implementation receiving its requested message count and completing the close handshake, plus permanent handshake failure classification.
- Latest-state handoff while no presentation consumer drains messages, cancellation cleanup, concurrent-run rejection, receive silence, and individual track expiry while the source remains current.
- Exact retry attempt budgets, minimum `Retry-After` delays, permanent error/protocol termination, connection deadlines, cancellation during backoff, explicit restart, and observable unexpected failures.
- Dropping a disconnected world and accepting a restarted server's new stream/order context after reconnection.
- The actual desktop client receiving real Kestrel publications, clearing its state/subscriber on cancellation, and classifying real HTTP 404/503 handshake responses and retry advice.

## Limits

The geometry/mask/trajectory fixtures and injected host sources are test inputs, not claimed live detections. Model HTTP tests use fixture handlers; backend transport tests use real localhost TCP/WebSocket connections and the real server/receiver. Native JPEG tests use synthetic images. No physical camera session, live Meta request, calibration UI, microphone input, IL2CPP run, generated-code execution, or real-time performance benchmark was performed. No model credentials were used. The portable libraries were exercised on the .NET 10 test host and in the Unity Editor's Mono runtime; this does not establish other platform compatibility. Agent navigation still requires independent target/collision checks and validation against real observations.
