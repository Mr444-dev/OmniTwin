using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Backend.Media;
using OmniTwin.Contracts;
using OmniTwin.Perception.Calibration;
using OmniTwin.Perception.Tracking;
using OmniTwin.Perception.Vision;
using OpenCvSharp;

namespace OmniTwin.Backend.Services;

/// <summary>Produces deterministic rendered frames and segments them locally; it never contacts a camera or model.</summary>
public sealed class SyntheticTabletopReplaySource : IWorldPerceptionSource
{
    private const int Width = 640;
    private const int Height = 480;
    private readonly BackendOptions _options;
    private readonly CameraPreviewStore _preview;
    private readonly TimeProvider _time;

    /// <summary>Creates a clearly labeled replay source using tactical-desk metric bounds.</summary>
    public SyntheticTabletopReplaySource(BackendOptions options, CameraPreviewStore preview, TimeProvider time)
    { _options = options ?? throw new ArgumentNullException(nameof(options)); _preview = preview ?? throw new ArgumentNullException(nameof(preview)); _time = time ?? throw new ArgumentNullException(nameof(time)); }

    /// <inheritdoc />
    public bool IsEnabled => true;
    /// <inheritdoc />
    public PerceptionSourceMode SourceMode => PerceptionSourceMode.SyntheticTabletopReplay;

    /// <inheritdoc />
    public async IAsyncEnumerable<PerceptionPublication> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var calibrationId = Guid.NewGuid();
        var profile = new CalibrationProfile(calibrationId, Width, Height,
            new[] { new PixelPoint(0, 0), new PixelPoint(Width - 1, 0), new PixelPoint(Width - 1, Height - 1), new PixelPoint(0, Height - 1) },
            new[] { new Vector2D(-10, -6.25f), new Vector2D(10, -6.25f), new Vector2D(10, 6.25f), new Vector2D(-10, 6.25f) },
            0.08f, 2.5f, 0.08f, 0.1f);
        var trackingSessionId = Guid.NewGuid();
        var tracker = new ObstacleTracker(trackingSessionId, calibrationId, _options.RetentionMilliseconds);
        long sequence = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capturedTicks = Stopwatch.GetTimestamp();
                var elapsedSeconds = sequence * 0.2;
                var capturedAt = _time.GetUtcNow();
                byte[] jpeg;
                ReplaySegmentation segmentation;
                using (var image = RenderFrame(sequence, elapsedSeconds))
                {
                    Cv2.ImEncode(".jpg", image, out jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 88));
                    if (jpeg.Length is 0 or > 8_388_608)
                        throw new IOException("Synthetic replay frame encoding failed.");
                    segmentation = SegmentLocalContours(image, profile, sequence, capturedAt.ToUnixTimeMilliseconds());
                }
                var frame = CapturedFrame.FromJpeg(jpeg, sequence, capturedAt, capturedTicks);
                var capturedElapsed = ToMilliseconds(capturedTicks);
                var batch = new ObstacleObservationBatch(sequence, capturedElapsed, capturedAt.ToUnixTimeMilliseconds(), segmentation.Observations);
                var now = ToMilliseconds(Stopwatch.GetTimestamp());
                var update = tracker.Update(batch, now, ObservationSource.LocalImageSegmentation);
                if (update != TrackingUpdateStatus.Accepted) throw new InvalidOperationException("Synthetic local tracker rejected a deterministic in-order frame: " + update + ".");
                var snapshot = tracker.Snapshot(now, _time.GetUtcNow().ToUnixTimeMilliseconds());
                _preview.Publish(frame, calibrationId, segmentation.Masks, trackingSessionId, segmentation.IsHazardTrack);
                yield return new PerceptionPublication(snapshot, null, 0, SourceMode);
                sequence++;
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _preview.Clear(); }
    }

    private static Mat RenderFrame(long sequence, double seconds)
    {
        var image = new Mat(Height, Width, MatType.CV_8UC3, new Scalar(11, 18, 24));
        for (var x = 0; x <= Width; x += 32) Cv2.Line(image, new Point(x, 0), new Point(x, Height - 1), new Scalar(23, 41, 48), 1, LineTypes.Link8);
        for (var y = 0; y <= Height; y += 32) Cv2.Line(image, new Point(0, y), new Point(Width - 1, y), new Scalar(23, 41, 48), 1, LineTypes.Link8);
        Cv2.Rectangle(image, new Point(12, 12), new Point(Width - 13, Height - 13), new Scalar(0, 105, 106), 2);
        var containerX = 174 + (int)Math.Round(30 * Math.Sin(seconds * 0.43));
        var containerY = 170 + (int)Math.Round(14 * Math.Sin(seconds * 0.31));
        Cv2.Rectangle(image, new Point(containerX, containerY), new Point(containerX + 76, containerY + 58), new Scalar(160, 112, 40), -1, LineTypes.Link8);
        Cv2.Rectangle(image, new Point(containerX, containerY), new Point(containerX + 76, containerY + 58), new Scalar(218, 166, 84), 2, LineTypes.Link8);

        var crateX = 322 + (int)Math.Round(25 * Math.Sin(seconds * 0.37 + 1.2));
        var crateY = 300 + (int)Math.Round(18 * Math.Cos(seconds * 0.29 + 0.4));
        Cv2.Rectangle(image, new Point(crateX, crateY), new Point(crateX + 58, crateY + 50), new Scalar(72, 178, 78), -1, LineTypes.Link8);
        Cv2.Rectangle(image, new Point(crateX, crateY), new Point(crateX + 58, crateY + 50), new Scalar(120, 224, 132), 2, LineTypes.Link8);

        var hazardX = 464 + (int)Math.Round(24 * Math.Sin(seconds * 0.4 + 0.7));
        var hazardY = 217 + (int)Math.Round(20 * Math.Cos(seconds * 0.33 + 0.9));
        Cv2.Circle(image, new Point(hazardX, hazardY), 27, new Scalar(10, 25, 220), -1, LineTypes.AntiAlias);
        Cv2.Circle(image, new Point(hazardX, hazardY), 27, new Scalar(42, 94, 255), 2, LineTypes.AntiAlias);
        Cv2.PutText(image, "SYNTHETIC TABLETOP REPLAY  /  LOCAL CV ONLY", new Point(22, 40), HersheyFonts.HersheySimplex,
            0.55, new Scalar(164, 232, 226), 1, LineTypes.AntiAlias);
        Cv2.PutText(image, "NO CAMERA  ·  NO SAM  ·  NO MODEL INFERENCE", new Point(22, 62), HersheyFonts.HersheySimplex,
            0.43, new Scalar(118, 177, 188), 1, LineTypes.AntiAlias);
        return image;
    }

    private static ReplaySegmentation SegmentLocalContours(Mat image, CalibrationProfile profile, long sequenceNumber, long capturedAtUnixMs)
    {
        using var containerMask = new Mat(); using var crateMask = new Mat(); using var hazardMask = new Mat();
        Cv2.InRange(image, new Scalar(90, 70, 10), new Scalar(220, 150, 80), containerMask);
        Cv2.InRange(image, new Scalar(25, 120, 30), new Scalar(130, 220, 150), crateMask);
        Cv2.InRange(image, new Scalar(0, 0, 120), new Scalar(75, 80, 255), hazardMask);
        var contours = new List<ReplayContour>(3);
        var nextId = 0;
        Append(containerMask, "container", false);
        Append(crateMask, "supply crate", false);
        Append(hazardMask, "hazard drum", true);
        contours.Sort(CompareContours);
        var observations = new ObstacleObservation[contours.Count];
        var masks = new MaskOverlaySample[contours.Count];
        var hazardTrackIds = new HashSet<int>();
        for (var index = 0; index < contours.Count; index++)
        {
            var contour = contours[index];
            observations[index] = contour.Observation;
            var trackId = index + 1;
            masks[index] = MaskOverlaySample.FromLocalImageSegmentation(trackId, contour.Left, contour.Top, contour.Right, contour.Bottom,
                contour.Observation.Confidence, contour.MaskWidth, contour.MaskHeight, contour.ForegroundMask, sequenceNumber, capturedAtUnixMs);
            if (contour.Observation.IsHazardZone) hazardTrackIds.Add(trackId);
        }
        return new ReplaySegmentation(Array.AsReadOnly(observations), Array.AsReadOnly(masks), hazardTrackIds);

        void Append(Mat mask, string label, bool hazard)
        {
            Cv2.FindContours(mask, out Point[][] detectedContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            foreach (var contour in detectedContours)
            {
                if (Cv2.ContourArea(contour) < 250) continue;
                var box = Cv2.BoundingRect(contour);
                if (box.Width < 12 || box.Height < 12) continue;
                var contactU = box.X + box.Width / 2.0;
                var contactV = box.Bottom - 0.5;
                if (!profile.Workspace.Mapper.TryPixelToWorld(contactU, contactV, out var world)) continue;
                var radius = (float)(Math.Max(box.Width, box.Height) * 10.0 / Width * 0.5 + profile.SafetyMarginMeters);
                radius = Math.Clamp(radius, profile.MinimumRadiusMeters + profile.SafetyMarginMeters, profile.MaximumRadiusMeters);
                if (!profile.Workspace.ContainsCircle(world, radius)) continue;
                var observation = new ObstacleObservation(nextId++, label, world, radius, 0.72f, hazard);
                using var region = new Mat(mask, box);
                var foreground = new byte[checked(box.Width * box.Height)];
                Marshal.Copy(region.Data, foreground, 0, foreground.Length);
                contours.Add(new ReplayContour(observation, box.X, box.Y, box.Right - 1, box.Bottom - 1, box.Width, box.Height, foreground));
            }
        }
    }

    private static int CompareContours(ReplayContour left, ReplayContour right)
    {
        var comparison = string.CompareOrdinal(left.Observation.Label, right.Observation.Label);
        if (comparison == 0) comparison = left.Observation.IsHazardZone.CompareTo(right.Observation.IsHazardZone);
        if (comparison == 0) comparison = left.Observation.Position.X.CompareTo(right.Observation.Position.X);
        if (comparison == 0) comparison = left.Observation.Position.Z.CompareTo(right.Observation.Position.Z);
        if (comparison == 0) comparison = left.Observation.RadiusMeters.CompareTo(right.Observation.RadiusMeters);
        if (comparison == 0) comparison = left.Observation.Confidence.CompareTo(right.Observation.Confidence);
        return comparison;
    }

    private sealed class ReplaySegmentation
    {
        private readonly HashSet<int> _hazardTrackIds;
        internal ReplaySegmentation(IReadOnlyList<ObstacleObservation> observations, IReadOnlyList<MaskOverlaySample> masks, HashSet<int> hazardTrackIds)
        { Observations = observations; Masks = masks; _hazardTrackIds = hazardTrackIds; }
        internal IReadOnlyList<ObstacleObservation> Observations { get; }
        internal IReadOnlyList<MaskOverlaySample> Masks { get; }
        internal bool IsHazardTrack(int id) => _hazardTrackIds.Contains(id);
    }

    private readonly record struct ReplayContour(ObstacleObservation Observation, int Left, int Top, int Right, int Bottom,
        int MaskWidth, int MaskHeight, byte[] ForegroundMask);

    private static long ToMilliseconds(long timestamp) => checked((long)(timestamp * 1000.0 / Stopwatch.Frequency));
}
