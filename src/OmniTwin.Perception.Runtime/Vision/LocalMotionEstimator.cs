using System;
using System.Collections.Generic;
using System.Linq;
using OmniTwin.Contracts;
using OmniTwin.Perception.Tracking;
using OpenCvSharp;

namespace OmniTwin.Perception.Vision;

/// <summary>A measured image displacement with its original SAM identity and independent capture age.</summary>
public sealed record LocalMotionEstimate(int TrackId, Vector2D Position, float Quality, long CaptureTimestamp, long CapturedAtUnixMs);

/// <summary>Measures sparse bidirectional optical flow inside verified object ROIs, with delayed-mask image alignment.</summary>
/// <remarks>Owns bounded grayscale images. Native work is serialized by a private lock and must run outside UI threads.</remarks>
public sealed class LocalMotionEstimator : IDisposable
{
    private readonly object _gate = new();
    private readonly CalibrationProfile _profile;
    private readonly List<Region> _regions = new();
    private Mat? _previous;
    private CapturedFrame? _latest;
    private bool _disposed;
    private bool _lost;
    private double _scale;

    /// <summary>Creates a session restricted to the calibrated camera and world polygons.</summary>
    public LocalMotionEstimator(CalibrationProfile profile) => _profile = profile ?? throw new ArgumentNullException(nameof(profile));

    /// <summary>Gets whether significant motion could not be resolved for a previously verified ROI.</summary>
    public bool IsTrackingLost { get { lock (_gate) return _lost; } }
    /// <summary>Gets the latest actual camera heartbeat, independently of SAM and track retention.</summary>
    public long? LastCaptureTimestamp { get { lock (_gate) return _latest?.CaptureTimestamp; } }

    /// <summary>Consumes one real camera frame. Lost regions remain lost until corrected by SAM.</summary>
    public void Observe(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (frame.Width != _profile.ImageWidth || frame.Height != _profile.ImageHeight)
                throw new ArgumentException("Motion frame dimensions disagree with calibration.", nameof(frame));
            if (_latest is not null && frame.SequenceNumber <= _latest.SequenceNumber) throw new ArgumentException("Motion frames must be ordered.", nameof(frame));
            var current = Gray(frame);
            try
            {
                if (_previous is not null)
                {
                    foreach (var region in _regions)
                    {
                        if (region.Lost) continue;
                        region.Measured = false;
                        var roi = Clip(region.Bounds, current.Size());
                        if (roi.Width < 6 || roi.Height < 6) { region.Lost = true; continue; }
                        using var mask = new Mat(_previous.Size(), MatType.CV_8UC1, Scalar.Black);
                        Cv2.Rectangle(mask, roi, Scalar.White, -1);
                        var points = Cv2.GoodFeaturesToTrack(_previous, 40, 0.03, 2, mask, 3, false, 0.04);
                        if (points.Length < 4)
                        {
                            if (frame.Change.ChangedFraction > 0.002) region.Lost = true;
                            continue;
                        }
                        var next = Array.Empty<Point2f>(); var back = Array.Empty<Point2f>();
                        Cv2.CalcOpticalFlowPyrLK(_previous, current, points, ref next, out var status, out var errors);
                        Cv2.CalcOpticalFlowPyrLK(current, _previous, next, ref back, out var reverse, out _);
                        var dx = new List<double>(); var dy = new List<double>();
                        for (var i = 0; i < points.Length; i++)
                            if (status[i] != 0 && reverse[i] != 0 && errors[i] < 30 &&
                                Distance(points[i], back[i]) < 1 && Distance(points[i], next[i]) < 30)
                            { dx.Add(next[i].X - points[i].X); dy.Add(next[i].Y - points[i].Y); }
                        if (dx.Count < 4) { region.Lost = true; continue; }
                        var x = Median(dx); var y = Median(dy);
                        var inliers = 0;
                        for (var i = 0; i < dx.Count; i++) if (Math.Abs(dx[i] - x) < 1.5 && Math.Abs(dy[i] - y) < 1.5) inliers++;
                        var quality = (float)inliers / points.Length;
                        if (quality < 0.5f) { region.Lost = true; continue; }
                        region.ShiftX += x; region.ShiftY += y; region.Quality = quality;
                        region.Bounds = new Rect2d(region.Bounds.X + x, region.Bounds.Y + y, region.Bounds.Width, region.Bounds.Height);
                        region.Measured = true;
                    }
                }
                _previous?.Dispose(); _previous = current; current = null!; _latest = frame;
                _lost = _regions.Any(region => region.Lost);
            }
            finally { current?.Dispose(); }
        }
    }

    /// <summary>Replaces anchors with the latest accepted SAM mask, aligned against the current camera image.</summary>
    public void Correct(SamSegmentationResult result, ObstacleTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(tracker);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _regions.Clear(); _lost = false;
            if (_previous is null || _latest is null) return;
            using var source = Gray(result.SourceFrame);
            foreach (var detection in result.Detections)
            {
                if (!tracker.TryGetVerifiedTrackId(result.SequenceNumber, detection.ObjectId, out var id, out var radius) ||
                    !MaskObstacleProjector.TryBaseSpan(detection, out var contact)) continue;
                var box = detection.Box;
                var roi = Clip(new Rect2d(box.Left * _scale - 3, box.Top * _scale - 3,
                    (box.Right - box.Left + 1) * _scale + 6, (box.Bottom - box.Top + 1) * _scale + 6), source.Size());
                var region = new Region(id, roi, (contact.Left + contact.Right) / 2, contact.V, radius);
                _regions.Add(region);
                if (roi.Width < 6 || roi.Height < 6) { region.Lost = true; continue; }
                if (_latest.SequenceNumber == result.SequenceNumber) continue;
                using var template = new Mat(source, roi);
                Cv2.MeanStdDev(template, out _, out var deviation);
                if (deviation.Val0 < 3) { region.Lost = true; continue; }
                using var scores = new Mat();
                Cv2.MatchTemplate(_previous, template, scores, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(scores, out _, out var best, out _, out var location);
                var suppression = new Rect(Math.Max(0, location.X - roi.Width / 2), Math.Max(0, location.Y - roi.Height / 2),
                    Math.Min(roi.Width, scores.Width - Math.Max(0, location.X - roi.Width / 2)),
                    Math.Min(roi.Height, scores.Height - Math.Max(0, location.Y - roi.Height / 2)));
                Cv2.Rectangle(scores, suppression, new Scalar(-1), -1);
                Cv2.MinMaxLoc(scores, out double _, out double second);
                if (!double.IsFinite(best) || best < 0.8 || best - second < 0.04) { region.Lost = true; continue; }
                region.ShiftX = location.X - roi.X; region.ShiftY = location.Y - roi.Y;
                region.Bounds = new Rect2d(location.X, location.Y, roi.Width, roi.Height);
                region.Quality = (float)best; region.Measured = true;
            }
            _lost = _regions.Any(region => region.Lost);
        }
    }

    /// <summary>Returns owned, bounded measurements; geometry outside the calibrated polygon is rejected.</summary>
    public IReadOnlyList<LocalMotionEstimate> ReadEstimates()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = new List<LocalMotionEstimate>(_regions.Count);
            if (_latest is null) return result.AsReadOnly();
            foreach (var region in _regions)
            {
                if (!region.Measured || region.Lost) continue;
                var u = region.BaseU + region.ShiftX / _scale; var v = region.BaseV + region.ShiftY / _scale;
                if (!_profile.Workspace.ContainsPixel(u, v) || !_profile.Workspace.Mapper.TryPixelToWorld(u, v, out var position) ||
                    !_profile.Workspace.ContainsCircle(position, region.Radius))
                { region.Lost = true; _lost = true; continue; }
                result.Add(new LocalMotionEstimate(region.Id, position, region.Quality, _latest.CaptureTimestamp, _latest.CapturedAt.ToUnixTimeMilliseconds()));
            }
            return result.AsReadOnly();
        }
    }

    private Mat Gray(CapturedFrame frame)
    {
        using var color = Cv2.ImDecode(frame.CopyJpeg(), ImreadModes.Grayscale);
        var gray = new Mat();
        _scale = Math.Min(1, 320.0 / frame.Width);
        Cv2.Resize(color, gray, new Size((int)Math.Round(frame.Width * _scale), (int)Math.Round(frame.Height * _scale)), interpolation: InterpolationFlags.Area);
        return gray;
    }
    private static Rect Clip(Rect2d rectangle, Size size)
    {
        var x = Math.Clamp((int)Math.Floor(rectangle.X), 0, size.Width);
        var y = Math.Clamp((int)Math.Floor(rectangle.Y), 0, size.Height);
        return new Rect(x, y, Math.Max(0, Math.Min(size.Width, (int)Math.Ceiling(rectangle.Right)) - x),
            Math.Max(0, Math.Min(size.Height, (int)Math.Ceiling(rectangle.Bottom)) - y));
    }
    private static double Distance(Point2f a, Point2f b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static double Median(List<double> values) { values.Sort(); return values[values.Count / 2]; }
    /// <summary>Releases all native grayscale storage after the session has stopped.</summary>
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _previous?.Dispose(); _regions.Clear(); _latest = null; } }
    private sealed class Region
    {
        internal Region(int id, Rect bounds, double baseU, double baseV, float radius)
        { Id = id; Bounds = new Rect2d(bounds.X, bounds.Y, bounds.Width, bounds.Height); BaseU = baseU; BaseV = baseV; Radius = radius; }
        internal int Id { get; }
        internal float Radius { get; }
        internal Rect2d Bounds;
        internal double BaseU, BaseV, ShiftX, ShiftY;
        internal float Quality = 1;
        internal bool Lost, Measured;
    }
}
