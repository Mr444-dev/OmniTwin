# Camera and SAM perception stage

The camera/SAM adapter stage produces validated image detections. The implemented [world tracking stage](tracking.md) connects these masks to homography, estimated footprints, persistent local IDs, and timed publications.

## Project boundary

`OmniTwin.Perception` remains a dependency-free core library targeting `netstandard2.1` and `net10.0`. It now includes `FrameChangeTrigger` and `SamOneBitMaskCodec`. `OmniTwin.Perception.Runtime` targets .NET 10 and owns OpenCvSharp4, HTTP, JSON parsing, capture, and segmentation orchestration. The CLI references this runtime project. Neither runtime project belongs inside Unity.

OpenCvSharp4 and its Windows native runtime are pinned to `4.13.0.20260627`. The bundled runtime was exercised on Windows x64 through native JPEG tests. Other operating systems need compatible OpenCvSharp native binaries and have not been tested here.

## Capture and scheduling

`CameraCaptureWorker.ReadFramesAsync` opens a local device on a background task using DirectShow on Windows. It requests 640x480 at 30 FPS by default; actual driver capabilities determine the delivered format/rate. It accepts BGR frames up to four megapixels, computes grayscale differences, encodes JPEG quality 85, and publishes owned image data through a capacity-one channel. Native matrices stay inside the producer. A mid-session resolution change fails explicitly.

Capture timestamps are taken when the host's native read returns. They include later preprocessing/inference delay in observation age, but do not reveal any earlier camera/driver buffering delay.

The single-producer `FrameChangeTrigger` compares consecutive grayscale pixels. A pixel is changed at intensity delta 20 or greater; motion requires at least 2% of pixels. The first frame triggers segmentation. Further triggers are spaced by at least 250 ms, refresh 100 ms after movement settles when spacing permits, and refresh at least every 1000 ms while new frames arrive. Thresholds schedule requests; they do not track objects or distinguish lighting changes from motion.

`SegmentationWorker` drains capture independently of inference. Its capacity-one queue replaces pending frames and calls the client serially. Frames/results have a default 12-second useful lifetime. A linked cancellation token stops the HTTP operation at the frame's remaining lifetime; expired results are rejected. A separate hybrid consumer measures local optical flow on intervening captures. API failures produce safe categories while capture continues; camera failure invalidates the session.

Cancellation stops queue waits, HTTP, delays, and producer iteration. An active native camera read depends on the driver returning; .NET cancellation cannot force it to finish. Consumers must dispose enumerators to end capture. A hard camera-driver deadline would require a separately supervised capture process. Native JPEG normalization is synchronous and must run off a UI/render thread.

## SAM protocol

The client posts one noun phrase and a JPEG data URL to `https://api.meta.ai/v1/responses`, with model `sam-3.1`, `stream: false`, and string metadata `mask_encoding: one_bit` / `include_confidence: true`. It uses the documented non-streaming image path. [Meta SAM request guide](https://dev.meta.ai/docs/sam/segmenting).

The response envelope must identify a completed SAM response and contain assistant output-text messages. Duplicate JSON properties, error/refusal/incomplete outputs, and invalid field types are rejected. Standard envelope metadata can contain additional fields. Geometry is parsed strictly: image frame zero, unique nonnegative object IDs, inclusive source-pixel boxes, matching source dimensions, matching finite confidences in [0,1], and complete mask data. [SAM output grammar](https://dev.meta.ai/docs/sam/reading-segmentation).

The requested `one_bit` format uses Meta's base85/range-coding codec, not generic base64 or plain packed bits. The adapted decoder enforces canonical base85 and range-code finalization. This stage rejects `lossless` (`~`) rather than guessing its decoding. Default limits are 128 detections, one megapixel per mask, four megapixels across masks, eight MiB of JSON, and four megapixels per source frame. Zero matches is a valid empty result. The codec's adaptation and fixture provenance are recorded in [the notice](../third-party/meta-sam/NOTICE.md).

Object IDs are response-local. Stable identity is documented for frames within a SAM video response; this client sends independent still images. Reusing the same integer from two image calls is not evidence that the physical object is the same. `ObstacleTracker` assigns local `SegmentedObstacle.Id` values independently of those source IDs. [SAM identity semantics](https://dev.meta.ai/docs/sam/reading-segmentation).

## Failure behavior

The default client permits three HTTP attempts. Each has a 6-second headers/body timeout; the operation deadline is 12 seconds, possibly shortened by remaining frame usefulness. Delays start at 250 ms, double and cap at five seconds. Only HTTP 408/429/500/502/503/504, transport failures and attempt timeouts are retried. Validation, authentication, access and redirects are permanent within that call.

`Retry-After` takes precedence when longer than the computed delay. If it cannot fit within the operation deadline, the client fails with `retry_exceeds_deadline` instead of retrying early. A spent deadline is `total_timeout`; exhausted per-attempt timeouts are `attempt_timeout`. A server may already have processed a request before a transport failure or timeout, so a retry can duplicate inference work and consume additional credits. No idempotency guarantee is assumed.

The environment factory loads `META_API_KEY`, disables HTTP redirects, and owns its HTTP transport. Injected clients must also disable redirects and must be disposed by their owner after active calls finish. Each client rejects a second overlapping operation rather than accumulating callers. Errors omit credentials, JPEG data, and provider response bodies.

No live service claim follows from the protocol tests. Account access, actual output variations, physical-camera behavior, capture-to-result latency, and sustained frame rate remain to be measured.

The worker/status/HUD use `auth_missing_or_invalid_401`, `permission_denied_403`, `rate_limited_429`, `upstream_timeout` and `network_error`. Missing `META_API_KEY` uses the authentication category. Neither tokens nor raw response bodies are forwarded. Lower-level CLI timing diagnostics remain available for one-shot troubleshooting.
