# Calibrated world tracking

This module connects decoded still-image SAM masks to immutable world-space tracking snapshots. It covers the queried object concept; observing `cup` does not detect every possible obstacle category.

## Configuration and calibration

`CalibrationProfile` loads a version-one closed JSON schema with a 16 KiB read limit. All keys are required; duplicate/unknown keys, nonfinite numbers, invalid IDs, invalid geometry, and a mismatched image size fail explicitly. The schema is shown in [the rectangular example](../examples/calibration.rectangular.json).

The example's camera corners describe pixel centers from `(0,0)` through `(639,479)` for a 640x480 image, mapped to a 20x12.5 meter simulation rectangle. It is a mathematical configuration example. A physical deployment needs four measured correspondences in matching perimeter order. Recalibration creates a new profile and tracking session; the mapper is never mutated in place.

`CalibratedWorkspace` owns both corner sets and checks camera points against a convex polygon. Its world-circle check requires the complete estimated radius to fit inside all polygon edges. The CLI verifies actual capture resolution before sending the frame to SAM.

Run schema/geometry validation offline:

```powershell
dotnet run --project src/OmniTwin.Perception.Cli -c Release -- calibration-check examples/calibration.rectangular.json
```

This command does not measure camera reprojection error. Use independently measured checkpoints to evaluate physical calibration accuracy.

## Mask to estimated footprint

`MaskObstacleProjector` treats the SAM box edges as inclusive source pixels. It maps the mask raster onto that pixel-area extent, preserving half-pixel boundaries when the mask is resized. The lowest occupied raster row sets the base edge. The lowest occupied 10% band supplies the horizontal support span, including full pixel-cell widths. Its midpoint and two endpoints are projected through homography.

The estimated radius is the larger projected endpoint distance, floored by `minimumRadiusMeters`, plus `safetyMarginMeters`. The projector applies confidence, maximum-radius, camera-ROI, finite-projection, and complete-world-circle checks. Rejected detections retain their response-local ID and an explicit reason. A rejected detection never becomes an invented origin or zero-radius obstacle.

This is a monocular base-span heuristic. It estimates lateral extent rather than reconstructing depth, height, overhangs, or a true ground-contact point. Occlusion, object shape, perspective, and lens distortion can bias it. A configurable margin is an engineering allowance; these checks do not certify a real robot's collision safety. Prototype navigation should use its own freshness and collision guardrails.

## Persistent association

`ObstacleTracker` accepts owned `ObstacleObservationBatch` values with a strictly increasing source sequence and nondecreasing monotonic capture time. Each observation carries a source ID for diagnostics. The matching cost uses world position, radius, label, and hazard designation; response-local source IDs are excluded from identity matching.

Candidate observations are sorted deterministically. For each retained track, a constant-velocity estimate predicts the next association position for at most one second, with speed capped at five meters per second by default. The default spatial gate is 1.5 meters. Radius ratios must lie in `[0.4, 2.5]`; labels and hazard flags must agree.

The rectangular Hungarian solver assigns tracks to observations or unmatched columns globally. Valid edge costs are normalized to `[0,1]`; unmatched cost exceeds the number of track rows, prioritizing maximum valid association cardinality before cost. Tests compare the assignment result against exhaustive search on small matrices. Online prediction plus assignment is a conventional tracking pattern; [SORT](https://arxiv.org/abs/1602.00763) is a primary reference for a Kalman/Hungarian combination. This implementation uses bounded constant velocity and world-distance/radius costs.

Unmatched observations receive increasing positive local IDs. Missing tracks retain their last observed position. IDs are never reused within a session. Capacity is bounded at 128; a batch that would exceed it is rejected atomically, preserving the prior source state and next ID. A new tracking session starts with a new GUID. Compare identities using session/calibration IDs together with the local obstacle ID.

This provides association-based continuity, not guaranteed physical re-identification. Indistinguishable objects, long occlusions, abrupt motion, or an unobserved crossing can still change identity. There is no local optical-flow update or appearance embedding in this stage.

## Age, confidence, and publication

Capture retains a process-local monotonic timestamp alongside UTC time. Inference duration therefore contributes to observation age. `CalibratedTrackingWorker` converts capture and current timestamps into one monotonic millisecond domain, serializes tracker operations under a short state lock, and publishes independently of the background inference consumer.

The nominal publication timer is 30 Hz. Retention defaults to 3000 ms from capture, with expiry at the exact limit. Snapshots keep the last measured position; prediction is used only for association. `Obstacle.Confidence` retains the model's last confidence, while `TrackingConfidence` is:

```text
lastDetectionConfidence * (1 - monotonicAgeMilliseconds / retentionMilliseconds)
```

Each publication has a schema version, session ID, calibration ID, increasing publication sequence, latest accepted camera sequence, and current publication UTC time. `SourceAgeMilliseconds` measures the latest accepted capture's age even when that capture contained zero accepted obstacles. `IsObservationCurrent` becomes false before the first accepted observation and at/after source retention expiry. A recent publication timestamp alone never establishes a fresh scene.

Per-track metadata includes original capture UTC time, monotonic age, decayed confidence, and whether it appeared in the latest accepted batch. A fresh empty observation and an expired empty scene have different source freshness metadata. Projection diagnostics describe excluded detections; a fresh capture with rejections does not establish that every part of the scene is understood.

API failures neither invent observations nor reset their age. The publisher continues aging/expiring tracks while inference waits, and exposes the latest failure category. Invalid calibration/image dimensions fail the session. Consumer backpressure coalesces timer ticks instead of creating an unbounded publication queue. The timer is a requested frequency; sustained latency and throughput have not been benchmarked.

## Run the world stream

After creating a measured `calibration.json` and configuring `META_API_KEY`:

```powershell
dotnet run --project src/OmniTwin.Perception.Cli -c Release -- watch-world 0 30 "cup" "calibration.json"
```

Optional `--hazard` assigns the requested concept a hazard flag. This is an operator designation. The command submits images to Meta and writes JSON world publications. The [backend host](backend.md) composes this same pipeline into a WebSocket stream. Live camera/model operation remains unverified. The Unity adapter has now been imported and exercised in Play mode; see unity-scene.md.
