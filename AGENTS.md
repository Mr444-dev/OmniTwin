# OmniTwin engineering constitution

Applies to every file in this repository. Build a verifiable digital twin; never claim unmeasured performance or an integration that does not exist.

## Required workflow
1. Before implementing a feature, name the exact paths and public interfaces affected; assess thread safety, latency, cancellation, and failure modes.
2. Implement complete, strongly typed behavior. No unfinished methods, dummy production data, empty exception handlers, or silently successful failures. Explicitly requested test fixtures and documented built-in fallback policies are allowed.
3. Document public APIs with XML comments. Validate external input before publishing immutable snapshots or directives.
4. Run an isolated verification command and relevant regression checks. Report actual results and remaining integration limits.

## Layer boundaries
- `OmniTwin.Contracts`: immutable values, snapshots, telemetry, mission commands, and `ISwarmPolicy`; no Unity, OpenCV, HTTP, or Roslyn references. Build for `netstandard2.1` and `net10.0`.
- `OmniTwin.Perception`: camera/audio acquisition, calibration, frame-change detection, tracking, and model adapters. Depends only on Contracts among domain layers.
- `OmniTwin.Brain`: planning, source verification, compilation, bounded self-correction, policy selection, and supervised execution. Depends on Contracts; does not mutate perception state.
- `OmniTwin.Simulation`: Unity presentation, navigation, physics, and tactical UI. Consume validated data and compiled-in local behavior on the Unity main thread.
- `OmniTwin.Backend` is the composition/transport host, not another location for duplicated domain implementations.

## Toolchain and source control
- Use .NET 10 LTS for the backend, runtime tools, and test hosts. Keep .NET Standard 2.1 outputs for Unity. The user's approved .NET 10 migration supersedes the original .NET 8 specification.
- Prefer the latest stable compatible SDK, dependencies, and Unity packages. Verify official releases before changing pins; previews require an explicit user request. Preserve conformance-fixture provenance independently of production package updates.
- Centralize NuGet versions in `Directory.Packages.props`, commit dependency lock files, and verify locked restore, warning-free build, and all affected tests after updates.
- Keep `Mr444-dev/OmniTwin` private. Commit and push verified milestones to its configured remote as requested by the user; verify remote visibility and synchronization. Never force-push or commit credentials, local recordings, build outputs, or Unity caches.
- The user creates the Unity project manually following `docs/unity-setup.md`. Do not install or replace their Editor/project without a request. Keep URP, Core RP, and Shader Graph aligned with the selected Editor's supported package set.

## Real-time behavior
- Never perform blocking HTTP, camera acquisition, compilation, or generated-code execution in Unity `Update` or `FixedUpdate`.
- Use cancellable asynchronous I/O and bounded queues/channels. Prefer the latest perception frame; prevent unbounded backlog, API overlap, and out-of-order updates.
- Snapshots must own their collections. Publish complete snapshots atomically; never expose mutable buffers across threads.
- Treat 30 Hz streaming and 60 FPS rendering as targets until measured. Track capture age, queue age, API duration, and end-to-end latency separately.
- Configure carving thresholds and stationary behavior explicitly. Carving and path replanning are not guaranteed to complete in the next physics step.

## Models and external I/O
- Preserve the requested Baseten model identifier exactly: `meta-models/muse-spark-open-1026`. Verify provider acceptance before enabling inference; never silently substitute another model.
- Load credentials from `META_API_KEY` and `BASETEN_API_KEY`; never commit or log secrets. Do not assume a `.env` file is loaded automatically.
- Check current official documentation before implementing provider request/response adapters. Do not invent endpoints, mask formats, provider features, or latency guarantees.
- Every external call needs cancellation, a bounded timeout, response-size limits, strict schema and semantic validation, and bounded exponential backoff for transient failures. Honor `Retry-After`; do not retry invalid input or authentication failures. Document duplicate-request/cost implications.
- A schema-invalid response never reaches simulation. Reject nonfinite numbers, invalid ranges, unknown schema versions, duplicate IDs, and stale observations at the ingestion boundary.
- Use SAM updates to correct local tracking; maintain stable IDs and explicit confidence/age. Local tracking consumes CPU and adds latency.

## Geometry and safety
- Homography maps points on one calibrated plane. Lens distortion, elevated objects, occlusion, camera movement, and uncertain contact points require separate handling.
- Reject duplicate, collinear, self-crossing, or numerically degenerate calibration corners. Never map a failed projection to a plausible world origin.
- Keep calibration immutable; replace the complete mapper after recalibration. Validate footprint and safety margin before issuing navigation targets.
- An AST filter, restricted references, Roslyn compilation, and `AssemblyLoadContext` are not a security sandbox. Do not instantiate generated classes in the backend host or Unity.
- Verify symbols against an explicit allowlist as well as syntax. Reject unsafe/native/reflection capabilities; compilation success does not establish runtime safety.
- Execute generated policies only behind OS-enforced isolation with resource/time limits and no inherited credentials. A process alone is not sufficient isolation. Unity receives validated directives, not executable generated assemblies.
- Bound source size, diagnostics, generation attempts, worker lifetime, output size, and directive counts. Maximum three generation/verification attempts total, then select a documented built-in policy or hold safely.
- Display concise decision summaries and verification diagnostics in the HUD; do not require or label hidden model reasoning as an audit log.

## Verification and documentation
- Preserve warning-free builds and meaningful mathematical, boundary, immutability, and serialization tests.
- Test the `netstandard2.1` binaries as well as the backend target. A successful library build does not prove Unity Editor, IL2CPP, navigation, camera, or live API compatibility.
- Keep README claims tied to implemented files and executed checks. Keep future integrations and measured behavior clearly identified.
