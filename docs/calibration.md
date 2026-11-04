# Camera calibration

Run commands from the directory containing `OmniTwin.sln`, using the SDK specified in `global.json`. The Unity `TacticalDesk` scene has a 20 × 12.5 m floor. This stage does not require Unity package changes.

The system SDK on this workstation may be older than the pinned 10.0.403 SDK. [First-run instructions](api-keys.md) explain how to install the required SDK or use the verified local .NET 10.0.403 SDK by updating `PATH` in the current terminal. The project keeps its pinned version.

## Capture an image from the same camera

Keep the camera fixed and mark four corners of a rectangular workspace on one flat surface. For example, a physical 80 × 50 cm area can map to 20 × 12.5 m in the simulation: 25 simulation meters per meter of tabletop. The profile describes simulation meters.

Stop perception and any other applications using the camera, then run:

```powershell
dotnet run --project src/OmniTwin.Perception.Cli -c Release -- snapshot 0 local/table.jpg
```

`0` is the camera index. The command uses the same OpenCV capture module as perception, requests 640 × 480, and reports the dimensions actually received. It saves one JPEG without EXIF metadata and makes no API request. It does not overwrite an existing file; use a new name such as `local/table-2.jpg` when repeating the capture. The final file is written after the camera source closes and the temporary file is complete; concurrent writes will not overwrite it either.

Cancellation is limited to 15 seconds. OpenCV driver calls cannot be forcibly interrupted, so a faulty driver may delay shutdown. Ctrl+C cancels managed operations. The first capture may need to be repeated while exposure stabilizes.

The `local/` directory is ignored by Git. Keep your own images and profiles there; do not force-add them to the repository.

## Select the four corners

Start the local backend in a separate terminal:

```powershell
dotnet run --project src/OmniTwin.Backend -c Release -- --OmniTwin:EnablePerception=false
```

Open the [calibration page](http://127.0.0.1:5108/calibration). No Meta key is required.

1. Open the captured JPEG. The image stays in the browser. Do not crop, rotate, or resize it.
2. Keep the workspace at 20 × 12.5 m for the current scene. A different profile scale requires matching floor and navigation dimensions in Unity.
3. Click the top-surface corners in order: top left, top right, bottom right, bottom left. Exclude the vertical front lip and the floor. Use **+ / −** to zoom and the viewport scrollbars to inspect corners; zoom keeps coordinates in the original image pixels. For a rotated workspace, keep perimeter order and choose the axes deliberately: +X runs from point 1 to 2, and +Z from point 1 to 4.
4. Undo a point, clear all points, or edit U/V coordinates numerically. Coordinates are pixels in the original image and are independent of the displayed size.
5. Click **Validate profile**. The server checks the schema and geometry. Correct the points if validation fails.
6. Click **Download calibration.json** and save the file as `local/calibration.json`. Changing points, scale, or image invalidates the previous download link. Each validation creates a new calibration ID.

![Calibration page checked in a browser with a marked synthetic image](images/calibration-browser.png)

The screenshot shows the actual interface with a marked synthetic test image; it is not a measurement of a physical tabletop.

The page accepts JPEG images up to 8 MiB and 4,194,304 pixels. It checks dimensions before decoding and rejects EXIF to avoid ambiguous rotation. It sends only profile JSON to the server. The backend does not save the image or profile and does not switch a running perception session.

Default profile parameters are a 0.05–3 m radius, a 0.1 m additional margin, and a 0.3 minimum confidence. They apply to estimated obstacles in the simulation and do not represent a physical safety measurement.

Four-corner homography corrects perspective for a flat tabletop, including an oblique camera angle. It does not correct curved edges caused by a wide-angle lens. If straight tabletop edges visibly bow, calibrate and undistort the camera with a checkerboard before taking the snapshot; do not draw curved workspace boundaries.

## Check the profile and mapping accuracy

```powershell
dotnet run --project src/OmniTwin.Perception.Cli -c Release -- calibration-check local/calibration.json
```

This check confirms the schema, ranges, and homography validity. Assess accuracy against independent measured points inside the workspace, including near its edges. For the first run, compare known object positions with the estimated positions in the simulation. Four fitting points alone do not prove accuracy.

Changing the camera position, framing, lens, resolution, or plane requires recalibration. The backend checks frame dimensions but cannot detect a framing change that keeps the same dimensions. A homography does not correct lens distortion, object height, or errors in the estimated contact point.

## Enable perception and Unity

Stop the calibration backend. In the terminal for the new process, set `META_API_KEY` using your chosen secret-management method. Do not put the key in Unity or the repository. `.env` files are not loaded automatically.

```powershell
$env:OmniTwin__CalibrationPath = (Resolve-Path .\local\calibration.json).Path
$env:OmniTwin__CameraDevice = "0"
$env:OmniTwin__EnablePerception = "true"
dotnet run --project src/OmniTwin.Backend -c Release
```

This opens the camera and may use Meta credits. It requires access to `sam-3.1`. The backend requests the profile resolution and rejects frames with different dimensions. The default concept is `cup`; set `OmniTwin__Concept` before startup to use a different single concept.

In Unity, open `Assets/_OmniTwin/Scenes/TacticalDesk.unity`, enter Play mode, and use the reconnect button in the HUD. A connected stream and a current observation are separate states. Source diagnostics are available at `/api/status` and `/health/ready`. See the [scene instructions](unity-scene.md) for the next steps.

## Interface limits

`POST /api/calibration/validate` requires JSON and the `X-OmniTwin-Calibration: 1` header. The supplied Origin must match the backend origin. The request body is limited to 16 KiB and five seconds. The page makes up to three attempts for transient failures, using exponential delay, `Retry-After`, and a 15-second total budget. Geometry errors are not retried. The tool uses the existing loopback host.

Tests use marked synthetic images and real Kestrel. Results and integration limits are recorded in the [verification log](verification.md).

## Repeatable browser check

The check requires Node.js 20 or later and a backend running with perception disabled. It does not open a camera or use API keys. It launches a separate headless browser, checks the actual file download and invalid input, and performs three attempts after controlled 503 responses. Those responses and the image are test inputs only; the final successful validation uses the real backend.

```powershell
Set-Location tests/CalibrationBrowser
npm ci
npx playwright install chromium --only-shell
npm test -- http://127.0.0.1:5108
```

The JSON result and page screenshot are saved under the ignored `TestResults/` directory at the repository root. The test uses [Playwright Library](https://playwright.dev/docs/library), pinned as a test dependency; the backend does not require Node.js or npm.
