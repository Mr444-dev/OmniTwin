using System;
using System.Threading;
using OmniTwin.Contracts;
using OmniTwin.Perception.Vision;
using OpenCvSharp;

namespace OmniTwin.Backend.Media;

/// <summary>An immutable bounded image with source identity; navigation never consumes this preview.</summary>
public sealed class CameraPreviewFrame
{
    private readonly byte[] _jpeg;
    internal CameraPreviewFrame(Guid session, Guid calibration, CapturedFrame frame, int width, int height, byte[] owned, long accepted, long initialAge)
    { SessionId = session; CalibrationId = calibration; SequenceNumber = frame.SequenceNumber; CapturedAtUnixMs = frame.CapturedAt.ToUnixTimeMilliseconds(); Width = width; Height = height; _jpeg = owned; Accepted = accepted; InitialAge = initialAge; }
    /// <summary>Gets the camera-source lifetime identity.</summary>
    public Guid SessionId { get; }
    /// <summary>Gets the calibration linked to this capture.</summary>
    public Guid CalibrationId { get; }
    /// <summary>Gets actual camera sequence.</summary>
    public long SequenceNumber { get; }
    /// <summary>Gets original capture time.</summary>
    public long CapturedAtUnixMs { get; }
    /// <summary>Gets bounded preview width.</summary>
    public int Width { get; }
    /// <summary>Gets bounded preview height.</summary>
    public int Height { get; }
    internal long Accepted { get; }
    internal long InitialAge { get; }
    /// <summary>Returns independent image bytes for HTTP delivery.</summary>
    public byte[] CopyJpeg() => (byte[])_jpeg.Clone();
}

/// <summary>Capacity-one five-Hz preview cache, independent of SAM completion and invalidated on capture loss.</summary>
public sealed class CameraPreviewStore
{
    private readonly object _gate = new(); private readonly TimeProvider _time;
    private Guid _session = Guid.NewGuid(); private CameraPreviewFrame? _frame; private long? _lastAttempt; private int _encoding;
    private string? _failure;
    /// <summary>Creates an empty store; it never opens a camera.</summary>
    public CameraPreviewStore(TimeProvider time) => _time = time;
    /// <summary>Gets a preview-specific failure; it does not alter navigation/perception state.</summary>
    public string? FailureCategory { get { lock (_gate) return _failure; } }
    /// <summary>Encodes at most five real frames per second, outside store locks. Call from the backend capture consumer.</summary>
    public bool Publish(CapturedFrame frame, Guid calibration)
    {
        ArgumentNullException.ThrowIfNull(frame); if (calibration == Guid.Empty) throw new ArgumentException("Calibration is required.");
        if (Interlocked.Exchange(ref _encoding, 1) != 0) return false;
        try
        {
            var now = _time.GetTimestamp(); Guid session;
            var age = _time.GetUtcNow().ToUnixTimeMilliseconds() - frame.CapturedAt.ToUnixTimeMilliseconds();
            if (age < -500 || age >= MediaProtocolLimits.PreviewFreshnessMilliseconds) return false;
            lock (_gate)
            {
                if (_lastAttempt.HasValue && _time.GetElapsedTime(_lastAttempt.Value, now).TotalMilliseconds < 200) return false;
                _lastAttempt = now; session = _session;
            }
            using var source = Cv2.ImDecode(frame.CopyJpeg(), ImreadModes.Color);
            if (source.Empty() || source.Width != frame.Width || source.Height != frame.Height) throw new ArgumentException("Preview dimensions disagree.");
            var scale = Math.Min(1, (double)MediaProtocolLimits.PreviewMaximumSide / Math.Max(source.Width, source.Height));
            using var small = new Mat(); Cv2.Resize(source, small, new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale))), 0, 0, InterpolationFlags.Area);
            Cv2.ImEncode(".jpg", small, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 70));
            if (jpeg.Length == 0 || jpeg.Length > MediaProtocolLimits.PreviewMaximumBytes) throw new ArgumentException("Preview encoding exceeds bounds.");
            lock (_gate)
            {
                if (session != _session || (_frame is not null && frame.SequenceNumber <= _frame.SequenceNumber)) return false;
                _frame = new CameraPreviewFrame(session, calibration, frame, small.Width, small.Height, jpeg, now, Math.Max(0, age)); _failure = null; return true;
            }
        }
        catch (Exception error) when (error is OpenCVException or ArgumentException)
        { lock (_gate) { _frame = null; _failure = "camera_preview_encoding_failed"; } return false; }
        finally { Volatile.Write(ref _encoding, 0); }
    }
    /// <summary>Returns only a capture whose effective monotonic age is below 750 ms.</summary>
    public CameraPreviewFrame? Read()
    {
        lock (_gate) return _frame is not null && _frame.InitialAge + _time.GetElapsedTime(_frame.Accepted).TotalMilliseconds < MediaProtocolLimits.PreviewFreshnessMilliseconds ? _frame : null;
    }
    /// <summary>Clears images and rotates identity at source shutdown/restart.</summary>
    public void Clear() { lock (_gate) { _frame = null; _session = Guid.NewGuid(); _lastAttempt = null; _failure = null; } }
}
