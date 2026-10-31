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
    private readonly ObjectSemanticService _semantics;
    /// <summary>Creates a source controlled by explicit backend settings.</summary>
    public CameraPerceptionSource(BackendOptions options, CameraPreviewStore preview, ObjectSemanticService semantics)
    { _options = options ?? throw new ArgumentNullException(nameof(options)); _preview = preview; _semantics = semantics; }
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
        try
        {
            await foreach (var update in tracking.RunHybridAsync(frames, client, cancellationToken,
                (frame, masks) => _preview.Publish(frame, profile.CalibrationId, masks, _semantics.SessionId, id => _semantics.IsHazard(id, _options.Hazard)),
                batch => { _semantics.Observe(batch); _preview.Correct(batch, id => _semantics.IsHazard(id, _options.Hazard)); }).ConfigureAwait(false))
                yield return new PerceptionPublication(_semantics.Decorate(update.Snapshot), update.LastFailureCategory, update.ProjectionRejections.Count);
        }
        finally { _preview.Clear(); _semantics.EndSession(); }
    }
}
