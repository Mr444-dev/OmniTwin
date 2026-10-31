# OmniTwin verification

Verified on 2026-10-31 on Windows x64 using .NET SDK 10.0.403 with domain targets `net10.0` and `netstandard2.1`; the test hosts run on .NET 10.

## Current perception, swarm and Brain acceptance

Locked restore and Release build completed with **zero warnings and zero errors**. All **366 tests passed**, zero failed/skipped: 121 .NET 10 shared, 121 .NET Standard shared, 55 native perception, 41 backend, and 28 Brain tests. Both shared hosts assert the loaded Policies target as well as the original libraries. The final run finished in 21.131 seconds on this workstation; this is a test-run observation, not an application benchmark. Additional tests cover safe LINQ/array enumeration, source complexity and malformed environment-key rejection without an HTTP request. `dotnet publish` also passed and deployed the trusted worker plus dependencies in `PolicyWorker/`.

New tests cover 6/12-second SAM configuration, five-second response acceptance, remaining useful-frame cancellation, 20-second verified retention, local/SAM age separation, OpenCV ROI translation and lost texture, delayed-mask correction, four deterministic policies, strict swarm/IPC schemas, real Kestrel telemetry/mission clients and idempotency. A real isolated-worker timeout selects PerimeterPatrol, then source failure selects EmergencyHold.

Brain tests use owned loopback HTTP provider fixtures for dated-ID 404 → alias, no alias substitution on auth/permission/rate failures, transient retry limits, closed model-output JSON, compiler feedback and three-candidate exhaustion. They also execute actual compiled C# in an **actual Windows AppContainer/Job Object**: token check, denied outside canary, blocked connection to a host-verified loopback listener, no inherited API keys, positive computation, infinite-loop kill, constructor exception, memory pressure, and NaN/Infinity rejection. Static capability tests cover full qualification, reflection/GetType, dynamic, typeof, GC, threading and file access. These are not live Baseten or Meta calls.

The real Unity scene contains **three ground and two aerial agents**. The final automatic navigation-only check advanced **1307 frames / 78 policy ticks** in an eight-second run. Displacements were **2.371, 7.997, 11.816, 10.407 and 7.154 m**, and maximum path vertex counts were **3, 2, 2, 2, 2**. Actual controllers/NavMesh moved around a temporary explicitly labeled authored obstacle, outside the SAM stream. The earlier 494-frame check also passed. The real disabled-perception backend received five measured rows and replied with five zero-speed directives; all five held with zero planar velocity. The saved NavMesh has 16 triangulation vertices. No physical observation is inferred from this fixture.

Native HUD clicks exercised Cordon and synthesis through the actual local HTTP path. With perception disabled, Brain safely reported `fallback / perception_not_current / attempt 0`, making no provider request. The scroll panel was checked in a small Game view, and its bottom remained at the footer boundary. After fixing input arbitration, a real wheel event over the HUD left camera position unchanged at `(8.2052, 25.5563, -16.3318)`; a wheel event over the world still changed zoom. Final sampled console ground truth had zero errors and no compilation failure. Expected source-silence warnings and existing Unity AI tooling entitlement/signature warnings are separate from application errors.

The importer was reverified against real build outputs and filesystem fixtures: **eight DLLs/four scripts**, repeat-import metadata preservation, local-edit rejection without ownership-record mutation, and duplicate-assembly rejection. Current libraries are reimported with only this project's Editor closed. [Swarm/Brain limits](brain.md), [current audit](project-audit.md).

The final import ownership hashes matched all twelve files, and the installed Transport DLL matched the verified Release output. After the last import, Unity sampled frame 2 and then 9647 with five Holding agents, zero planar velocities, `perception_not_enabled` and protocol `omnitwin.v2`. The backend received telemetry sequence 1183 for all five agents with five zero-speed directives. Final console ground truth: `compilationFailed=false`, `compiling=false`, `consoleErrors=0`; eight warnings included existing AI tooling and expected source silence. [Current Game-view capture](images/tactical-swarm.png) shows actual disabled-source state. No replacement detections were injected.

The sections below retain earlier calibration/transport checks as historical evidence; their 291-test count and earlier seven-DLL scene describe that preceding milestone.

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

The subsequent requirements audit and key-entry guide did not change the application binaries. The new `tools/SetOmniTwinApiKey.ps1` parsed successfully and was exercised through an actual masked terminal prompt using a test-only value: set, presence-only status, clear, and rejection of whitespace while preserving an existing value all passed. No provider request or persistent credential file was created. Local Markdown links in the audit, key guide, engineering decisions, and README were also checked. These are tooling checks; the .NET count above remains 291 from the preceding application verification.

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

Geometry/mask/trajectory images and injected test sources are test inputs, not claimed live detections. HTTP provider fixtures and real local server/receiver checks do not establish hosted model access. No physical camera/live provider session, microphone, standalone/IL2CPP run or sustained performance benchmark has been performed. Calibration browser checks, isolated generated-code execution and controlled Unity navigation have been exercised as described above. No model credentials were used. Real tabletop accuracy, live obstacle navigation and complete end-to-end model demonstration remain open acceptance criteria.
