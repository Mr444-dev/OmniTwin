# Engineering decisions

## Scope and ownership

The later four-layer constitution takes precedence over the earlier tree that placed vision and brain implementations inside the backend. This module therefore uses `OmniTwin.Contracts` and `OmniTwin.Perception.Calibration`. The backend composes perception and transport; Brain remains a subsequent module. No empty projects or nonfunctional provider adapters have been added.

Positions use Unity's X/Z plane in world meters. Image U increases rightward and V downward. The corner correspondence establishes world-axis directions; neither image axis is automatically inverted. Mapping an 80 cm desk width onto a 20 m simulation width establishes a scale of 25, not a measurement of the desk in true physical meters.

Compared with the supplied draft, roles and agent statuses are enums, snapshots include schema version and sequence metadata, and snapshot constructors own their collections. Sequence numbers need a producer-session identifier at the future transport layer to distinguish reconnects. Timestamp freshness and target safety cannot be established by a DTO constructor.

## Homography and numerical behavior

For a point on the calibrated plane, the projective relationship is:

```text
lambda * [X, Z, 1]^T = H * [u, v, 1]^T
X = (h00*u + h01*v + h02) / (h20*u + h21*v + h22)
Z = (h10*u + h11*v + h12) / (h20*u + h21*v + h22)
```

Four correspondences provide eight independent homogeneous equations for nine coefficients up to scale. The implementation centers each point set and scales its mean distance to sqrt(2), then solves the 8-by-9 system using row and column pivoting. It keeps the normalized transform for evaluation instead of recombining large coordinate offsets. This is an exact four-point solve, not RANSAC or a least-squares estimator. The planar model and normalization follow the concepts explained by [OpenCV's homography tutorial](https://docs.opencv.org/4.x/d9/dab/tutorial_homography.html).

The implementation rejects nonconvex or unordered polygons, repeated/collinear corners, normalized corner cross-products at or below `1e-8`, pivots at or below `1e-12` times the initial matrix magnitude, and normalized corner fit errors above `1e-7`. It also rejects a horizon crossing inside the calibration polygon. These numerical guards are not a camera-accuracy certification.

Runtime projection rejects nonfinite input, a near-zero homogeneous denominator relative to its terms (`1e-12`), and world output outside finite float range. It never returns a successful projection at an invented origin. `TryPixelToWorld` returns a default out value on failure, which callers must ignore.

Four points fit their own measurements and cannot independently quantify measurement error. Use additional measured checkpoints before relying on calibration accuracy. The plane assumption does not correct lens distortion, elevated mask pixels, occlusion, camera movement, or uncertain object contact. A mask's bottom-center point is only a heuristic for contact. Recalibrate when the camera or working plane moves. [OpenCV describes the planar assumptions](https://docs.opencv.org/4.x/d9/dab/tutorial_homography.html).

## Generated C# execution boundary

The supplied namespace/identifier denylist can miss aliases and other language/runtime capabilities. Successful compilation also says nothing about infinite loops, resource exhaustion, constructor effects, or invalid directives. Static verification is useful evidence; it cannot substitute for execution isolation. Microsoft explicitly states that untrusted code cannot be safely loaded into a trusted .NET process and recommends OS or virtualization boundaries. [Microsoft plugin guidance](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support).

For the future brain module, compile and inspect code without host-side activation. Execute only under OS-enforced permissions and resource limits, without inherited API credentials. A separate process must also be constrained; process separation alone does not remove file/network permissions. Unity should receive validated target data. The selected three-attempt interpretation means three total generation/verification attempts, followed by a documented built-in policy or safe hold. Show decision summaries and compiler diagnostics in the HUD.

## Provider facts checked on 2026-10-30

The project preserves the user's required Baseten ID `meta-models/muse-spark-open-1026`. Public Baseten examples use `meta-models/muse-spark-open`; the exact dated identifier has not been confirmed by an authenticated request. Future integration must fail clearly if the requested ID is unavailable and must not silently substitute another ID. Baseten also documents that Muse Spark Open does not support its structured-output or JSON modes, so local validation and bounded correction will be necessary. [Baseten announcement](https://www.baseten.co/resources/changelog/muse-spark-open-and-nemotron-35-super-vl-available-on-baseten/), [Model APIs documentation](https://docs.baseten.co/inference/model-apis/overview).

Meta documents SAM as `sam-3.1`, transcription as `muse-voice-transcribe-1.0`, and image generation as `muse-image-1.0`. Muse Wonder produces text and supports agentic/coding work; it is not an alternative image-generation model. Segmentation request and mask formats must be taken from the segmentation guide when implementing the adapter. [Meta model catalog](https://dev.meta.ai/docs/models).

The supplied millisecond ranges, zero-latency local tracking claim, and 60+ FPS target have not been measured in this repository. They are not acceptance results. A future benchmark must separate capture, preprocessing, queueing, inference, network, and rendering time.

## Navigation behavior

The correct Unity properties include `NavMeshObstacle.carving` and `NavMeshObstacle.carvingMoveThreshold`. With stationary-only carving, moving obstacles use local avoidance while carved holes are removed; carving resumes after the stationary-time condition. Moving-carve behavior is configured separately. Immediate next-physics-step global replanning is not guaranteed by enabling carving. [Unity carving API](https://docs.unity.com/en-us/engine/6000.5/script-reference/unityengine/ai/navmeshobstacle/carving).
