# Stable dependency baseline

Checked against official release metadata and registries on **2026-10-30**. Prefer the newest stable version compatible with the selected runtime and Editor; upgrade version pins and dependency lock files together, then verify the complete solution. Preview packages are opt-in.

| Component | Selected version | Scope |
| --- | --- | --- |
| .NET SDK | 10.0.403 | `global.json`, stable patches within 10.0.4xx allowed |
| .NET / ASP.NET Core | 10.0.14 | Runtime bundled with this SDK, backend/CLI/test hosts |
| C# | 14.0 | Externally built projects; Unity retains its own script compiler |
| Shared library targets | net10.0; netstandard2.1 | Backend and Unity API boundary |
| System.Text.Json | 10.0.14 | Explicit dependency of the .NET Standard transport target |
| OpenCvSharp4 / runtime.win | 4.13.0.20260627 | Matching managed/native Windows packages |
| Microsoft.Testing.Extensions.TrxReport | 2.5.2 | MTP test reports |
| xunit.v3 | 4.0.2 | Current xUnit framework, standalone test executables |
| Playwright | 1.64.1 | Optional isolated calibration browser checks, Node.js 20+; no backend runtime dependency |
| actions/checkout | 7.0.1 | CI, pinned commit |
| actions/setup-dotnet | 6.0.0 | CI, pinned commit |
| actions/upload-artifact | 7.0.2 | CI, pinned commit |
| Unity Editor | 6000.6.3f1 | Explicit user selection; keep pinned, installed manually |
| URP | 17.6.0 | Resolved in the user-created Unity 6.6 project |
| AI Navigation | 2.0.16 | Installed; reference floor NavMesh built in the Editor |
| Input System | 1.20.0 | User-created project's resolved version; camera and HUD input checked |
| Cinemachine | 6.6.0 | User-installed; retained, not required by the current orbit camera |

These are the actual Unity package versions recorded in the user-created project's manifest and lock file, superseding provisional pre-creation suggestions. Existing user-installed AI Assistant and Pipeline tooling include pre-release versions and were preserved; this integration did not upgrade Unity packages. Keep URP, Core RP, and Shader Graph aligned with the Editor's resolved set. Raw registry `dist-tags.latest` is not an Editor compatibility policy. Preserve package files, the project version, assets, and `.meta` files. [Integrated scene](unity-scene.md).

.NET 10 is LTS through **2028-11-14**; .NET 8 support ends **2026-11-10**. .NET 11 is still a release candidate on the verification date. The backend upgrade does not switch Unity's Mono/IL2CPP runtime to .NET 10. [Microsoft lifecycle](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [SDK release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json), [Unity API profiles](https://docs.unity3d.com/6000.6/Documentation/Manual/dotnet-profile-support.html).

NuGet versions live in `Directory.Packages.props`. Each project commits a `packages.lock.json`; CI uses `dotnet restore --locked-mode`. To intentionally update dependencies, edit the central pins, run `dotnet restore OmniTwin.sln --force-evaluate`, inspect lock-file changes, then build and test. `dotnet package list --outdated` reports stable direct update candidates; review compatibility before accepting them. Dependabot opens weekly NuGet and GitHub Actions update PRs; it does not merge or change Unity packages automatically. SDK and Editor release checks remain explicit maintenance work.

Tests use the .NET 10 Microsoft.Testing.Platform runner selected in `global.json`, with xUnit's MTP v2 integration. Invoke `dotnet test --solution OmniTwin.sln`; the previous positional solution/VSTest logger command is no longer the test entry point. `--report-trx` enables the installed report extension. The old VSTest SDK and adapter dependencies have been removed. [Microsoft test-runner documentation](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test), [xUnit MTP integration](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform).

The optional Python fixture generator remains pinned to `meta-sam-parser==0.1.0`, the documented upstream implementation used to establish the committed codec fixtures. It is not a production or .NET build dependency. Updating that reference is a separate conformance change requiring fixture comparison and attribution review.

Primary sources: [NuGet catalog](https://api.nuget.org/v3/index.json), [xUnit 4.0.2 release](https://xunit.net/releases/v3/4.0.2), [OpenCvSharp releases](https://github.com/shimat/opencvsharp/releases), [Unity Editor release](https://unity.com/releases/editor/whats-new/6000.6.3f1), [AI Navigation registry](https://packages.unity.com/com.unity.ai.navigation), [Input System registry](https://packages.unity.com/com.unity.inputsystem), [Cinemachine registry](https://packages.unity.com/com.unity.cinemachine), [checkout releases](https://github.com/actions/checkout/releases), [setup-dotnet releases](https://github.com/actions/setup-dotnet/releases), [upload-artifact releases](https://github.com/actions/upload-artifact/releases).
