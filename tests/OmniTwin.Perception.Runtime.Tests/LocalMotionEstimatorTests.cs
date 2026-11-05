using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Perception.Calibration;
using OmniTwin.Perception.Tracking;
using OmniTwin.Perception.Vision;
using OpenCvSharp;
using Xunit;

namespace OmniTwin.Perception.Runtime.Tests;

public sealed class LocalMotionEstimatorTests
{
    private static CalibrationProfile Profile() => new(Guid.NewGuid(), 200, 160,
        new[] { new PixelPoint(0, 0), new PixelPoint(199, 0), new PixelPoint(199, 159), new PixelPoint(0, 159) },
        new[] { new Vector2D(0, 0), new Vector2D(9.95f, 0), new Vector2D(9.95f, 7.95f), new Vector2D(0, 7.95f) });
    private static CapturedFrame Frame(long sequence, int dx = 0, int dy = 0, bool visible = true)
    {
        using var image = new Mat(160, 200, MatType.CV_8UC3, new Scalar(12, 12, 12));
        var random = new Random(12345);
        if (visible)
            for (var y = 50; y < 90; y++) for (var x = 60; x < 100; x++)
            { var value = (byte)random.Next(40, 250); image.Set(y + dy, x + dx, new Vec3b(value, value, value)); }
        Cv2.ImEncode(".jpg", image, out var jpeg);
        return new CapturedFrame(sequence, DateTimeOffset.UtcNow, 200, 160, jpeg, new FrameChangeDecision(0.1, true));
    }
    private static SamSegmentationResult Result(CapturedFrame frame) => new("native_image_test", frame, 0,
        Array.AsReadOnly(new[] { new SamDetection(7, new PixelBox(60, 50, 99, 89), 0.9f, 40, 40, Enumerable.Repeat((byte)1, 1600).ToArray()) }));

    private static CapturedFrame TexturedBackgroundFrame(long sequence, int objectDx = 0, int objectDy = 0)
    {
        using var image = new Mat(160, 200, MatType.CV_8UC3, new Scalar(12, 12, 12));
        for (var y = 46; y < 94; y++) for (var x = 56; x < 104; x++)
        {
            var value = (byte)(((x + y) & 1) == 0 ? 35 : 220);
            image.Set(y, x, new Vec3b(value, value, value));
        }
        var random = new Random(4567);
        for (var y = 0; y < 24; y++) for (var x = 0; x < 24; x++)
        {
            var value = (byte)random.Next(40, 250);
            image.Set(58 + objectDy + y, 68 + objectDx + x, new Vec3b(value, value, value));
        }
        Cv2.ImEncode(".jpg", image, out var jpeg);
        return new CapturedFrame(sequence, DateTimeOffset.UtcNow, 200, 160, jpeg, new FrameChangeDecision(0.1, true));
    }

    private static SamSegmentationResult MaskedObjectResult(CapturedFrame frame)
    {
        var mask = new byte[40 * 40];
        for (var y = 8; y < 32; y++) for (var x = 8; x < 32; x++) mask[y * 40 + x] = 1;
        return new SamSegmentationResult("masked_object_test", frame, 0,
            Array.AsReadOnly(new[] { new SamDetection(7, new PixelBox(60, 50, 99, 89), 0.9f, 40, 40, mask) }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasuresActualImageTranslationAndAlignsDelayedSamMask(bool delayed)
    {
        var profile = Profile(); var first = Frame(1); var second = Frame(2, 6, 4);
        var result = Result(first); var tracker = new ObstacleTracker(Guid.NewGuid(), profile.CalibrationId);
        var observation = Assert.Single(new MaskObstacleProjector(profile).Project(result, "cup", false).Observations);
        tracker.Update(new ObstacleObservationBatch(1, 0, 100000, new[] { observation }), 0);
        Assert.True(tracker.TryGetVerifiedTrackId(result.SequenceNumber, 7, out _, out _));
        using var motion = new LocalMotionEstimator(profile);
        motion.Observe(first);
        if (delayed) motion.Observe(second);
        motion.Correct(result, tracker);
        if (!delayed) motion.Observe(second);
        Assert.False(motion.IsTrackingLost);
        var estimate = Assert.Single(motion.ReadEstimates());
        Assert.Equal(1, estimate.TrackId);
        Assert.InRange(estimate.Position.X - observation.Position.X, 0.27f, 0.33f);
        Assert.InRange(estimate.Position.Z - observation.Position.Z, 0.17f, 0.23f);
        Assert.InRange(estimate.Quality, 0.5f, 1);
        Assert.Empty(motion.ReadMaskOverlays(first.SequenceNumber));
        var mask = Assert.Single(motion.ReadMaskOverlays(second.SequenceNumber));
        Assert.Equal(ObservationSource.LocalMotionEstimate, mask.ObservationSource);
        Assert.Equal(result.SequenceNumber, mask.SamSequenceNumber); Assert.Equal(second.SequenceNumber, mask.ImageSequenceNumber);
        Assert.InRange(mask.ShiftU, 5.4, 6.6); Assert.InRange(mask.ShiftV, 3.4, 4.6);
    }

    [Fact]
    public void LostTextureStopsMeasurementsAndFreshSamResetsLoss()
    {
        var profile = Profile(); var frame = Frame(1); var result = Result(frame);
        var tracker = new ObstacleTracker(Guid.NewGuid(), profile.CalibrationId);
        var observations = new MaskObstacleProjector(profile).Project(result, "cup", false).Observations;
        tracker.Update(new ObstacleObservationBatch(1, 0, 100000, observations), 0);
        using var motion = new LocalMotionEstimator(profile);
        motion.Observe(frame); motion.Correct(result, tracker); motion.Observe(Frame(2, visible: false));
        Assert.True(motion.IsTrackingLost); Assert.Empty(motion.ReadEstimates()); Assert.Empty(motion.ReadMaskOverlays(2));
        var restored = Frame(3); motion.Observe(restored);
        tracker.Update(new ObstacleObservationBatch(3, 200, 100200, observations), 200);
        motion.Correct(Result(restored), tracker);
        Assert.False(motion.IsTrackingLost);
        Assert.Equal(ObservationSource.SamVerified, Assert.Single(motion.ReadMaskOverlays(3)).ObservationSource);
    }

    [Fact]
    public async Task LostLocalOpticalFlowDoesNotMarkFreshSamObservationDegraded()
    {
        var profile = Profile();
        var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CallbackSamClient(frame => Task.FromResult(Result(frame)));

        async IAsyncEnumerable<CapturedFrame> Frames([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Frame(1);
            await verified.Task.WaitAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(220), cancellationToken);
            yield return Frame(2, visible: false);
        }

        var overlaysAtSecondFrame = -1;
        var worker = new CalibratedTrackingWorker(profile, "rubber duck");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        WorldPerceptionUpdate? publication = null;
        await foreach (var update in worker.RunHybridAsync(Frames(timeout.Token), client, timeout.Token,
            onFrame: (frame, overlays) => { if (frame.SequenceNumber == 2) overlaysAtSecondFrame = overlays.Count; },
            onVerified: _ => verified.TrySetResult()))
        {
            publication = update;
            if (overlaysAtSecondFrame >= 0) break;
        }

        Assert.NotNull(publication);
        Assert.Equal(0, overlaysAtSecondFrame);
        Assert.True(publication.Snapshot.IsObservationCurrent);
        Assert.Equal(ObservationSource.SamVerified, Assert.Single(publication.Snapshot.Obstacles).ObservationSource);
        Assert.Null(publication.LastFailureCategory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalMotionUsesSamForegroundInsteadOfStationaryBackgroundTexture(bool delayed)
    {
        var profile = Profile(); var first = TexturedBackgroundFrame(1); var second = TexturedBackgroundFrame(2, 6, 4);
        var result = MaskedObjectResult(first); var tracker = new ObstacleTracker(Guid.NewGuid(), profile.CalibrationId);
        var observation = Assert.Single(new MaskObstacleProjector(profile).Project(result, "cup", false).Observations);
        tracker.Update(new ObstacleObservationBatch(1, 0, 100000, new[] { observation }), 0);
        using var motion = new LocalMotionEstimator(profile);
        motion.Observe(first);
        if (delayed) motion.Observe(second);
        motion.Correct(result, tracker);
        if (!delayed) motion.Observe(second);

        Assert.False(motion.IsTrackingLost);
        var overlay = Assert.Single(motion.ReadMaskOverlays(second.SequenceNumber));
        Assert.InRange(overlay.ShiftU, 5.4, 6.6);
        Assert.InRange(overlay.ShiftV, 3.4, 4.6);
        var estimate = Assert.Single(motion.ReadEstimates());
        Assert.False(motion.IsTrackingLost);
        Assert.InRange(estimate.Position.X - observation.Position.X, 0.27f, 0.33f);
        Assert.InRange(estimate.Position.Z - observation.Position.Z, 0.17f, 0.23f);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "auth_missing_or_invalid_401")]
    [InlineData(HttpStatusCode.Forbidden, "permission_denied_403")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited_429")]
    [InlineData(HttpStatusCode.GatewayTimeout, "upstream_timeout")]
    public void ClassifiesHttpFailuresWithoutProviderData(HttpStatusCode status, string category) =>
        Assert.Equal(category, new SamApiException("http_error", 1, status).OperatorCategory);

    private sealed class CallbackSamClient(Func<CapturedFrame, Task<SamSegmentationResult>> segment) : ISam31Client
    {
        public Task<SamSegmentationResult> SegmentAsync(CapturedFrame frame, string nounPhrase,
            CancellationToken cancellationToken = default) => segment(frame);
    }
}
