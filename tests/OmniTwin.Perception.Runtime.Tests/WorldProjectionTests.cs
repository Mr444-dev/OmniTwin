using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Perception.Calibration;
using OmniTwin.Perception.Tracking;
using OmniTwin.Perception.Vision;
using Xunit;

namespace OmniTwin.Perception.Runtime.Tests;

public sealed class WorldProjectionTests
{
    private static readonly Guid CalibrationId = Guid.Parse("445c047b-81fa-43e9-a110-c792694a7fe3");
    private static readonly PixelPoint[] CameraCorners =
        { new(0, 0), new(100, 0), new(100, 100), new(0, 100) };
    private static readonly Vector2D[] WorldCorners =
        { new(-5, -5), new(5, -5), new(5, 5), new(-5, 5) };
    private static CalibrationProfile Profile(float maximumRadius = 3, float margin = 0.1f) =>
        new(CalibrationId, 101, 101, CameraCorners, WorldCorners, maximumRadiusMeters: maximumRadius, safetyMarginMeters: margin);

    private static SamSegmentationResult Result(long sequence = 0, PixelBox? box = null, byte[]? mask = null,
        float confidence = 0.9f, int width = 101, int maskWidth = 10, int maskHeight = 10)
    {
        var frame = new CapturedFrame(sequence, DateTimeOffset.UtcNow, width, 101, new byte[] { 255, 216, 255, 217 }, new FrameChangeDecision(1, true));
        var detection = new SamDetection(7, box ?? new PixelBox(40, 40, 59, 59), confidence, maskWidth, maskHeight,
            mask ?? Enumerable.Repeat((byte)1, maskWidth * maskHeight).ToArray());
        return new SamSegmentationResult("resp_fixture", frame, 0, Array.AsReadOnly(new[] { detection }));
    }

    private static string Json() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, calibrationId = CalibrationId, imageWidth = 101, imageHeight = 101,
        cameraCorners = CameraCorners.Select(point => new { u = point.U, v = point.V }),
        worldCorners = WorldCorners.Select(point => new { x = point.X, z = point.Z }),
        minimumRadiusMeters = 0.05, maximumRadiusMeters = 3.0, safetyMarginMeters = 0.1, minimumConfidence = 0.3
    });

    [Fact]
    public void ProjectsResizedInclusiveBoxAndAddsClearanceInWorldMeters()
    {
        var result = new MaskObstacleProjector(Profile()).Project(Result(), "cup", true);
        Assert.Empty(result.Rejections);
        var observation = Assert.Single(result.Observations);
        Assert.InRange(observation.Position.X, -0.05001f, -0.04999f);
        Assert.InRange(observation.Position.Z, 0.94999f, 0.95001f);
        Assert.InRange(observation.RadiusMeters, 1.09999f, 1.10001f);
        Assert.Equal(7, observation.SourceObjectId);
        Assert.True(observation.IsHazardZone);
    }

    [Fact]
    public void UsesLowestOccupiedBandRatherThanTheBoxOrUpperForeground()
    {
        var mask = new byte[100];
        mask[0] = 1;
        for (var x = 2; x <= 6; x++) mask[7 * 10 + x] = 1;
        var observation = Assert.Single(new MaskObstacleProjector(Profile()).Project(Result(mask: mask), "cup").Observations);
        Assert.InRange(observation.Position.X, -0.15001f, -0.14999f);
        Assert.InRange(observation.Position.Z, 0.54999f, 0.55001f);
        Assert.InRange(observation.RadiusMeters, 0.59999f, 0.60001f);
    }

    [Fact]
    public void HandlesSinglePixelMasksAndRadiusFloor()
    {
        var observation = Assert.Single(new MaskObstacleProjector(Profile()).Project(
            Result(box: new PixelBox(50, 50, 50, 50), mask: new byte[] { 1 }, maskWidth: 1, maskHeight: 1), "marker").Observations);
        Assert.Equal(0, observation.Position.X);
        Assert.InRange(observation.Position.Z, 0.04999f, 0.05001f);
        Assert.InRange(observation.RadiusMeters, 0.14999f, 0.15001f);
    }

    [Theory]
    [InlineData("low", ProjectionRejectionReason.LowConfidence)]
    [InlineData("empty", ProjectionRejectionReason.EmptyMask)]
    [InlineData("camera", ProjectionRejectionReason.OutsideCameraWorkspace)]
    [InlineData("world", ProjectionRejectionReason.OutsideWorldWorkspace)]
    [InlineData("radius", ProjectionRejectionReason.RadiusLimit)]
    public void ReportsProjectionRejectionsWithoutInventingWorldPositions(string scenario, ProjectionRejectionReason expected)
    {
        var profile = Profile(scenario == "radius" ? 0.9f : 3);
        var detection = scenario switch
        {
            "low" => Result(confidence: 0.1f),
            "empty" => Result(mask: new byte[100]),
            "camera" => Result(box: new PixelBox(0, 40, 19, 59)),
            "world" => Result(box: new PixelBox(40, 80, 59, 99)),
            _ => Result()
        };
        var projection = new MaskObstacleProjector(profile).Project(detection, "cup");
        Assert.Empty(projection.Observations);
        Assert.Equal(expected, Assert.Single(projection.Rejections).Reason);
    }

    [Fact]
    public void RejectsDimensionMismatchAndInvalidLabels()
    {
        var projector = new MaskObstacleProjector(Profile());
        Assert.Throws<InvalidDataException>(() => projector.Project(Result(width: 100), "cup"));
        Assert.Throws<ArgumentException>(() => projector.Project(Result(), " "));
    }

    [Fact]
    public void ParsesCompleteClosedCalibrationSchema()
    {
        var profile = CalibrationProfile.Parse(Encoding.UTF8.GetBytes(Json()));
        Assert.Equal(CalibrationId, profile.CalibrationId);
        Assert.Equal(101, profile.ImageWidth);
        Assert.Equal(101, profile.ImageHeight);
        Assert.True(profile.Workspace.ContainsCircle(default, 1));
        Assert.Equal(0.3f, profile.MinimumConfidence);
    }

    [Fact]
    public void RejectsMissingUnknownDuplicateAndIllTypedCalibrationFields()
    {
        var invalid = new[]
        {
            Json().Replace("\"schemaVersion\":1", "\"schemaVersion\":2"),
            Json().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            Json().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"extra\":1"),
            Json().Replace("\"schemaVersion\":1,", ""),
            Json().Replace("\"imageWidth\":101", "\"imageWidth\":\"101\""),
            Json().Replace("\"u\":0", "\"u\":0,\"u\":1"),
            Json().Replace(CalibrationId.ToString(), "invalid-id"),
            Json().Replace("\"minimumConfidence\":0.3", "\"minimumConfidence\":1e9999"),
            "{}"
        };
        foreach (var json in invalid) Assert.Throws<FormatException>(() => CalibrationProfile.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Throws<InvalidDataException>(() => CalibrationProfile.Parse(new byte[16_385]));
        Assert.Throws<ArgumentException>(() => new CalibrationProfile(CalibrationId, 100, 101, CameraCorners, WorldCorners));
        Assert.Throws<ArgumentException>(() => new CalibrationProfile(CalibrationId, 101, 101, CameraCorners, WorldCorners, safetyMarginMeters: 4));
    }

    [Fact]
    public async Task ReadsBoundedCalibrationFilesAndPropagatesCancellation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "calibration-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, Json(), TestContext.Current.CancellationToken);
            Assert.Equal(CalibrationId, (await CalibrationProfile.LoadAsync(path, TestContext.Current.CancellationToken)).CalibrationId);
            await File.WriteAllBytesAsync(path, new byte[16_385], TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => CalibrationProfile.LoadAsync(path, TestContext.Current.CancellationToken));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CalibrationProfile.LoadAsync(path, cancelled.Token));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PublishesAndExpiresDuringBlockedInferenceWithoutMovingObservedGeometry()
    {
        var clock = new AdjustableClock();
        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<SegmentationUpdate> Source([EnumeratorCancellation] CancellationToken token = default)
        {
            var result = Result(1);
            yield return new SegmentationUpdate(1, result, null);
            consumed.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
        var worker = new CalibratedTrackingWorker(Profile(), "cup", retentionMilliseconds: 3000, timeProvider: clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = worker.RunAsync(Source(stop.Token), stop.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var first = reader.MoveNextAsync().AsTask();
        await consumed.Task.WaitAsync(stop.Token);
        Assert.True(await first);
        while (reader.Current.Snapshot.Obstacles.Count == 0) Assert.True(await reader.MoveNextAsync());
        var observed = Assert.Single(reader.Current.Snapshot.Obstacles);
        Assert.Equal(1, reader.Current.Snapshot.SourceSequenceNumber);
        var beforeSequence = reader.Current.Snapshot.SequenceNumber;
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(4));
        Assert.True(await reader.MoveNextAsync());
        Assert.Empty(reader.Current.Snapshot.Obstacles);
        Assert.False(reader.Current.Snapshot.IsObservationCurrent);
        Assert.True(reader.Current.Snapshot.SourceAgeMilliseconds >= 4000);
        Assert.True(reader.Current.Snapshot.SequenceNumber > beforeSequence);
        Assert.InRange(observed.Obstacle.WorldPosition.Z, 0.94999f, 0.95001f);
    }

    [Fact]
    public async Task PreservesLatestAcceptedSourceOnReplay()
    {
        async IAsyncEnumerable<SegmentationUpdate> Source()
        {
            await Task.Yield();
            yield return new SegmentationUpdate(2, Result(2, confidence: 0.1f), null);
            yield return new SegmentationUpdate(1, Result(1), null);
        }
        WorldPerceptionUpdate? last = null;
        await foreach (var publication in new CalibratedTrackingWorker(Profile(), "cup").RunAsync(Source(), TestContext.Current.CancellationToken)) last = publication;
        Assert.NotNull(last);
        Assert.Equal(2, last.Snapshot.SourceSequenceNumber);
        Assert.Equal("tracking_out_of_order", last.LastFailureCategory);
        Assert.Empty(last.Snapshot.Obstacles);
    }

    [Fact]
    public async Task PublishesPerDetectionProjectionReasonsInAnOwnedResult()
    {
        async IAsyncEnumerable<SegmentationUpdate> Source()
        {
            await Task.Yield();
            yield return new SegmentationUpdate(1, Result(1, confidence: 0.1f), null);
        }
        WorldPerceptionUpdate? last = null;
        await foreach (var publication in new CalibratedTrackingWorker(Profile(), "cup").RunAsync(Source(), TestContext.Current.CancellationToken)) last = publication;
        Assert.NotNull(last);
        Assert.Equal(ProjectionRejectionReason.LowConfidence, Assert.Single(last.ProjectionRejections).Reason);
        Assert.Empty(last.Snapshot.Obstacles);
        Assert.True(last.Snapshot.IsObservationCurrent);
        Assert.Throws<NotSupportedException>(() => ((IList<ProjectionRejection>)last.ProjectionRejections).Clear());
    }

    [Fact]
    public async Task ReportsApiFailuresWithoutInventingOrRefreshingObservations()
    {
        async IAsyncEnumerable<SegmentationUpdate> Source()
        {
            await Task.Yield();
            yield return new SegmentationUpdate(1, Result(1), null);
            yield return new SegmentationUpdate(2, null, "attempt_timeout");
        }
        WorldPerceptionUpdate? last = null;
        await foreach (var publication in new CalibratedTrackingWorker(Profile(), "cup").RunAsync(Source(), TestContext.Current.CancellationToken)) last = publication;
        Assert.NotNull(last);
        Assert.Equal(1, last.Snapshot.SourceSequenceNumber);
        Assert.Equal("attempt_timeout", last.LastFailureCategory);
        Assert.Single(last.Snapshot.Obstacles);
    }

    [Fact]
    public async Task PropagatesCalibrationMismatchInsteadOfContinuingWithWrongGeometry()
    {
        async IAsyncEnumerable<SegmentationUpdate> Source()
        {
            await Task.Yield();
            yield return new SegmentationUpdate(1, Result(1, width: 100), null);
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in new CalibratedTrackingWorker(Profile(), "cup").RunAsync(Source(), TestContext.Current.CancellationToken))
                Assert.Fail("A mismatched image cannot produce a tracking publication.");
        });
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private long _offset;
        public override long GetTimestamp() => Stopwatch.GetTimestamp() + Interlocked.Read(ref _offset);
        internal void AdvanceMonotonic(TimeSpan duration) => Interlocked.Add(ref _offset,
            (long)(duration.TotalSeconds * Stopwatch.Frequency));
    }
}
