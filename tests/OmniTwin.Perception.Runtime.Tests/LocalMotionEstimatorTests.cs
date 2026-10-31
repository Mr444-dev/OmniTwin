using System;
using System.Linq;
using System.Net;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasuresActualImageTranslationAndAlignsDelayedSamMask(bool delayed)
    {
        var profile = Profile(); var first = Frame(1); var second = Frame(2, 6, 4);
        var result = Result(first); var tracker = new ObstacleTracker(Guid.NewGuid(), profile.CalibrationId);
        var observation = Assert.Single(new MaskObstacleProjector(profile).Project(result, "cup", false).Observations);
        tracker.Update(new ObstacleObservationBatch(1, 0, 100000, new[] { observation }), 0);
        using var motion = new LocalMotionEstimator(profile);
        motion.Observe(first);
        if (delayed) motion.Observe(second);
        motion.Correct(result, tracker);
        if (!delayed) motion.Observe(second);
        var estimate = Assert.Single(motion.ReadEstimates());
        Assert.False(motion.IsTrackingLost);
        Assert.Equal(1, estimate.TrackId);
        Assert.InRange(estimate.Position.X - observation.Position.X, 0.27f, 0.33f);
        Assert.InRange(estimate.Position.Z - observation.Position.Z, 0.17f, 0.23f);
        Assert.InRange(estimate.Quality, 0.5f, 1);
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
        Assert.True(motion.IsTrackingLost); Assert.Empty(motion.ReadEstimates());
        var restored = Frame(3); motion.Observe(restored);
        tracker.Update(new ObstacleObservationBatch(3, 200, 100200, observations), 200);
        motion.Correct(Result(restored), tracker);
        Assert.False(motion.IsTrackingLost);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "auth_missing_or_invalid_401")]
    [InlineData(HttpStatusCode.Forbidden, "permission_denied_403")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited_429")]
    [InlineData(HttpStatusCode.GatewayTimeout, "upstream_timeout")]
    public void ClassifiesHttpFailuresWithoutProviderData(HttpStatusCode status, string category) =>
        Assert.Equal(category, new SamApiException("http_error", 1, status).OperatorCategory);
}
