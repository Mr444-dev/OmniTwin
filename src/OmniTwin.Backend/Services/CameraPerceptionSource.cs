using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Perception.Tracking;
using OmniTwin.Perception.Vision;
using OmniTwin.Backend.Media;

namespace OmniTwin.Backend.Services;

/// <summary>Composes calibration, camera capture, SAM inference, and timed world tracking.</summary>
public sealed class CameraPerceptionSource : IWorldPerceptionSource
{
    private readonly BackendOptions _options;
    private readonly CameraPreviewStore _preview;
    /// <summary>Creates a source controlled by explicit backend settings.</summary>
    public CameraPerceptionSource(BackendOptions options, CameraPreviewStore preview) { _options = options ?? throw new ArgumentNullException(nameof(options)); _preview = preview; }
    /// <inheritdoc />
    public bool IsEnabled => _options.EnablePerception;
    /// <inheritdoc />
    public async IAsyncEnumerable<PerceptionPublication> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) throw new InvalidOperationException("Camera perception is disabled.");
        var profile = await CalibrationProfile.LoadAsync(_options.CalibrationPath!, cancellationToken).ConfigureAwait(false);
        using var client = Sam31Client.FromEnvironment();
        var camera = new CameraCaptureWorker(_options.CameraDevice, profile.ImageWidth, profile.ImageHeight);
        var frames = CalibratedFrameSource.ValidateAsync(camera.ReadFramesAsync(cancellationToken), profile, cancellationToken);
        var tracking = new CalibratedTrackingWorker(profile, _options.Concept, _options.Hazard, _options.RetentionMilliseconds);
        async IAsyncEnumerable<CapturedFrame> Preview([EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var frame in frames.WithCancellation(token).ConfigureAwait(false)) { _preview.Publish(frame, profile.CalibrationId); yield return frame; }
        }
        try
        {
            await foreach (var update in tracking.RunHybridAsync(Preview(cancellationToken), client, cancellationToken).ConfigureAwait(false))
                yield return new PerceptionPublication(update.Snapshot, update.LastFailureCategory, update.ProjectionRejections.Count);
        }
        finally { _preview.Clear(); }
    }
}
