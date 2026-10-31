using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Perception.Vision;

namespace OmniTwin.Perception.Tracking;

/// <summary>A fresh tracking publication and the latest inference/projection diagnostics.</summary>
public sealed class WorldPerceptionUpdate
{
    internal WorldPerceptionUpdate(ObstacleTrackingSnapshot snapshot, string? failure, IReadOnlyList<ProjectionRejection> rejections)
    { Snapshot = snapshot; LastFailureCategory = failure; ProjectionRejections = rejections; }
    /// <summary>Gets the immutable session/calibration-scoped world tracks.</summary>
    public ObstacleTrackingSnapshot Snapshot { get; }
    /// <summary>Gets the latest operation failure, cleared by a subsequent accepted batch.</summary>
    public string? LastFailureCategory { get; }
    /// <summary>Gets per-detection diagnostics from the most recent projected result.</summary>
    public IReadOnlyList<ProjectionRejection> ProjectionRejections { get; }
}

/// <summary>Associates calibrated observations while publishing and expiring tracks independently of inference.</summary>
public sealed class CalibratedTrackingWorker
{
    private readonly CalibrationProfile _profile;
    private readonly string _label;
    private readonly bool _hazard;
    private readonly long _retentionMs;
    private readonly double _publicationHz;
    private readonly TimeProvider _time;

    /// <summary>Configures a new session per enumeration, with bounded retention and publication frequency.</summary>
    public CalibratedTrackingWorker(CalibrationProfile profile, string label, bool isHazardZone = false,
        long retentionMilliseconds = PerceptionTiming.VerifiedRetentionMilliseconds, double publicationHz = 30, TimeProvider? timeProvider = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(label) || label.Length > 128) throw new ArgumentException("A bounded semantic label is required.", nameof(label));
        if (retentionMilliseconds < 1 || retentionMilliseconds > 60_000) throw new ArgumentOutOfRangeException(nameof(retentionMilliseconds));
        if (!double.IsFinite(publicationHz) || publicationHz < 1 || publicationHz > 120) throw new ArgumentOutOfRangeException(nameof(publicationHz));
        _label = label; _hazard = isHazardZone; _retentionMs = retentionMilliseconds; _publicationHz = publicationHz;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Publishes on a timer while segmentation runs on a background task. Recalibration requires a new enumeration.</summary>
    /// <remarks>Injected time providers and input capture timestamps must share a monotonic timestamp clock domain.</remarks>
    public IAsyncEnumerable<WorldPerceptionUpdate> RunAsync(IAsyncEnumerable<SegmentationUpdate> updates,
        CancellationToken cancellationToken = default) => RunCoreAsync(updates, null, cancellationToken);

    /// <summary>Tracks every camera frame locally while one bounded SAM request runs asynchronously.</summary>
    public async IAsyncEnumerable<WorldPerceptionUpdate> RunHybridAsync(IAsyncEnumerable<CapturedFrame> frames, ISam31Client client,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames); ArgumentNullException.ThrowIfNull(client);
        using var motion = new LocalMotionEstimator(_profile);
        async IAsyncEnumerable<CapturedFrame> Observe([EnumeratorCancellation] CancellationToken token = default)
        {
            await foreach (var frame in frames.WithCancellation(token).ConfigureAwait(false))
            { motion.Observe(frame); yield return frame; }
        }
        var segmentation = new SegmentationWorker(client, TimeSpan.FromMilliseconds(Math.Min(PerceptionTiming.MaximumFrameAgeMilliseconds, _retentionMs - 1)), _time);
        await foreach (var update in RunCoreAsync(segmentation.RunAsync(Observe(cancellationToken), _label, cancellationToken), motion, cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    private async IAsyncEnumerable<WorldPerceptionUpdate> RunCoreAsync(IAsyncEnumerable<SegmentationUpdate> updates, LocalMotionEstimator? motion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (updates is null) throw new ArgumentNullException(nameof(updates));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stateGate = new object();
        var tracker = new ObstacleTracker(Guid.NewGuid(), _profile.CalibrationId, _retentionMs);
        var projector = new MaskObstacleProjector(_profile);
        string? lastFailure = null;
        IReadOnlyList<ProjectionRejection> rejections = Array.Empty<ProjectionRejection>();
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in updates.WithCancellation(stop.Token).ConfigureAwait(false))
                {
                    if (update.Result is null)
                    {
                        lock (stateGate) lastFailure = update.FailureCategory ?? "missing_result";
                        continue;
                    }
                    var result = update.Result;
                    var projected = projector.Project(result, _label, _hazard);
                    var batch = new ObstacleObservationBatch(result.SequenceNumber, Milliseconds(result.CaptureTimestamp),
                        result.CapturedAt.ToUnixTimeMilliseconds(), projected.Observations);
                    lock (stateGate)
                    {
                        var status = tracker.Update(batch, Milliseconds(_time.GetTimestamp()));
                        rejections = projected.Rejections;
                        lastFailure = status switch
                        {
                            TrackingUpdateStatus.Accepted => null,
                            TrackingUpdateStatus.OutOfOrder => "tracking_out_of_order",
                            TrackingUpdateStatus.StaleOrFuture => "tracking_stale_or_future",
                            TrackingUpdateStatus.CapacityExceeded => "tracking_capacity_exceeded",
                            TrackingUpdateStatus.IdentityExhausted => "tracking_identity_exhausted",
                            _ => throw new InvalidOperationException("Unknown tracking update status.")
                        };
                        if (status == TrackingUpdateStatus.Accepted && motion is not null)
                        {
                            motion.Correct(result, tracker);
                            ApplyMotion();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        }, CancellationToken.None);
        void ApplyMotion()
        {
            if (motion is null) return;
            var now = Milliseconds(_time.GetTimestamp());
            foreach (var estimate in motion.ReadEstimates())
                tracker.ApplyLocalMotion(estimate.TrackId, estimate.Position, Milliseconds(estimate.CaptureTimestamp),
                    estimate.CapturedAtUnixMs, estimate.Quality, now);
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / _publicationHz), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (consumer.IsFaulted) await consumer.ConfigureAwait(false);
                var completed = consumer.IsCompleted;
                WorldPerceptionUpdate publication;
                lock (stateGate)
                {
                    ApplyMotion();
                    var cameraStale = motion?.LastCaptureTimestamp is long capture &&
                        _time.GetElapsedTime(capture).TotalMilliseconds >= PerceptionTiming.LocalMotionAgeMilliseconds;
                    publication = new WorldPerceptionUpdate(tracker.Snapshot(Milliseconds(_time.GetTimestamp()),
                        _time.GetUtcNow().ToUnixTimeMilliseconds()), lastFailure ?? (cameraStale ? "camera_frame_timeout" :
                        motion?.IsTrackingLost == true ? "local_motion_lost" : null), rejections);
                }
                yield return publication;
                if (completed) break;
            }
        }
        finally { stop.Cancel(); await consumer.ConfigureAwait(false); }
    }

    private long Milliseconds(long timestamp)
    {
        if (timestamp < 0) throw new InvalidOperationException("Negative capture timestamp.");
        return _time.GetElapsedTime(0, timestamp).Ticks / TimeSpan.TicksPerMillisecond;
    }
}
