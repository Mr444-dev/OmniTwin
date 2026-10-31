# Desktop Unity stream client

The portable connection client and Unity adapters are implemented and imported into `unity/OmniTwin.Unity`. Canonical adapter source remains in `unity/client/Runtime`; the import record identifies deployed copies. The actual project also contains the wired tactical scene, HUD, camera controls, materials, and a baked reference-floor NavMesh. [Run the integrated scene](unity-scene.md). The importer can update the portable libraries or seed a separate compatible project; it does not create projects or modify package manifests.

## Import

The existing `unity/OmniTwin.Unity` project uses Unity's ownership-checked importer. With the pinned .NET SDK installed, from the repository root:

```powershell
./tools/InstallUnityClient.ps1 -ProjectPath ./unity/OmniTwin.Unity
```

The default mode requires the target Editor to be closed. To update the currently open project without closing it, pass `-LiveEditor` and the full Unity CLI executable path:

```powershell
./tools/InstallUnityClient.ps1 -ProjectPath ./unity/OmniTwin.Unity -LiveEditor -UnityCliPath 'C:\Users\<user>\AppData\Local\Unity\bin\unity.exe'
```

The live mode verifies the exact open project, source and prior ownership hashes, then groups the copy under `AssetDatabase.StartAssetEditing`. It keeps existing `.meta` files, rolls changed files back on a write error, and waits for Unity to import and compile before Play mode.

If `dotnet` is not on PATH, pass `-DotNetPath` with the absolute SDK executable. The installer performs locked restores, builds `netstandard2.1`, and resolves runtime DLL paths through MSBuild. It imports Contracts, Perception core, Policies, Transport and JSON dependencies, plus seven canonical scripts (network, obstacle spawner, agent controller, executor, media console, LiDAR sensor, visual-services controller). The graph resolves **nine DLLs and seven scripts**. No backend, Roslyn, native OpenCV runtime or `net10.0` binary enters Unity. Existing metadata and local-edit protection are preserved.

The ownership record `OmniTwinClient.import.json` stores relative paths and SHA-256 hashes. Repeat imports preserve `.meta` files, reject locally edited/unowned destination files, and reject duplicate DLL names elsewhere in Assets. Removal of an old imported file requires manual review; the installer does not delete assets. Write failures are reported as failures, not successful imports. Reopen the target Editor, allow Unity to generate/import `.meta` files, and commit these files with the imported assets. The installer passed filesystem tests and was then used for the actual OmniTwin Editor import. Package-provided assembly conflicts and platform compatibility still require Editor verification.

## Wire the scene

1. Keep the project's URP camera and light. Use a horizontal floor at world Y = 0 and the world X/Z dimensions from your actual calibration. Leave the visualizer container at a fixed identity transform.
2. Create an empty GameObject named `TwinConnection`. Add **TwinWebSocketClient**. Its default endpoint is `ws://127.0.0.1:5108/ws/twin`.
3. Create an empty GameObject named `TrackedObstacles`. Add **DynamicObstacleSpawner**, and drag `TwinConnection` into its **Stream** field. Set the desired proxy height; the system does not infer physical height.
4. Start the backend with your calibration, camera, and Meta credentials according to [backend.md](backend.md). Enter Play mode after compilation completes.
5. Inspect `TwinWebSocketClient.View.State.Phase`, `FailureCategory`, and `TryGetCurrentView` from application code. The spawner creates cylinders only from real current observations. A disabled/no-source backend produces no obstacles.
6. After correcting a terminal connection problem, use the component's **Reconnect world stream** context-menu action, or disable/re-enable the component. Exit Play mode to cancel its worker.

The visualizer owns its generated roots; it recreates identity scope when the backend stream, tracking session, or calibration changes. Radius/position come from validated observations. Hazard color reflects the operator-configured hazard flag, not a chemical classifier. It interpolates positions on the main thread using unscaled frame time. It removes individual tracks when their retention expires, including time spent in transit or waiting in the local cache. On source silence, failure, rejection diagnostics, component disable, or disconnect, it marks `IsWorldCurrent` false and hides the retained display. This is loss of current information, not a claim that the floor is clear. Any future agent controller must stop accepting motion directives while the source is not current.

Each generated root has a capsule `NavMeshObstacle`: carving enabled, stationary-only carving enabled, movement threshold 0.05 m, stationary time 0.2 s. A baked NavMesh and agent controller are not created by this import. Moving obstacles rely on local avoidance until the stationary carving conditions are met; next-frame replanning is not guaranteed. [Unity obstacle API](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/AI.NavMeshObstacle.html).

## Connection and thread ownership

`WorldStreamClient.RunAsync` is a single producer; concurrent runs are rejected. It publishes one immutable state reference with `Volatile.Write`; `GetView` reads it atomically. New packets replace the previous state without queuing presentation work. `GetView` reevaluates source/delivery/cache/silence freshness even when the network is idle. `WorldStreamClientView.IsTrackCurrent` separately checks individual track retention. Readers never invoke network work or wait for a producer lock.

`TwinWebSocketClient.OnEnable` starts the loop through `Task.Run`; the loop uses cancellable asynchronous connection/receive calls. It does not call Unity APIs. `Update` only reads state and emits a sanitized warning for a terminal fault; the spawner performs all GameObject, material, interpolation, and navigation-component changes on the main thread. Disable cancels without synchronously joining the worker. The continuation observes completion and disposes its cancellation source. Every enable gets a distinct client/worker, preventing an old run from writing into the new component's state.

A run permits three connection attempts by default, including reconnects after successful connections. Each connection has a five-second handshake timeout; each complete message has a ten-second deadline including silence and a fragmentation/size bound. Retry delays start at 250 ms and double, capped at one minute, with a maximum of ten attempts configurable. Recognized permanent handshakes and protocol/schema violations terminate immediately. Recognized transient connection failures, receive failures, and closed streams use the remaining attempt budget. No message carries over into a disconnected or new connection's state. Every connection gets a fresh order guard so a restarted backend can start a new stream.


`LidarSensor` scans 48 beams at up to 20 Hz from each agent's physics transform, observes authored static colliders, carved obstacles and other agents, reports sample age/hit count/minimum range, and keeps scanning when beam rendering is hidden. The local swept-body guard fails closed after 250 ms without a scan and checks upcoming motion against nearby colliders; it is a simulation sensor, not a physical robot safety certificate. See [visual systems](visual-systems.md).

The .NET 10 connector collects HTTP handshake status and `Retry-After`. The portable .NET Standard connector uses those details only if its runtime exposes an `HttpWebResponse`; it also recognizes selected underlying socket failures. An unclassified handshake failure terminates immediately rather than retrying an unknown authentication/protocol failure. Retry advice over one minute is terminal. This platform difference is explicit: automatic retries for every possible Unity/Mono handshake are not claimed. [Microsoft response-detail API](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocketoptions.collecthttpresponsedetails?view=net-10.0).

The initial scope is the Windows desktop Editor/Mono client. The project was imported and run on **6000.6.3f1 / Universal 3D**, including local-backend connection, source-unavailable handling, HUD reconnect interaction, advancing frames, and codec validation on Mono. These checks do not establish WebGL, standalone-player, IL2CPP/AOT, live camera/API, or moving-agent navigation behavior.

## Isolated verification

```powershell
dotnet build OmniTwin.sln -c Release
dotnet test --solution OmniTwin.sln -c Release --no-build --report-trx
./tests/UnityImport/Verify-UnityImport.ps1
```

Shared tests execute the connection state machine against both library targets. Real loopback tests exercise the actual desktop connector and backend, including handshake status/retry advice. Filesystem import fixtures check real DLL/script copying, repeat imports, GUID metadata preservation, and rejection of local edits and duplicate assemblies. No fake source is added to the production host or Unity adapter. [Executed results and remaining limits](verification.md).
