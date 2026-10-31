using System;
using OmniTwin.Contracts;
using OmniTwin.Perception.Tracking;
using OmniTwin.Transport;
using Xunit;

namespace OmniTwin.Tests;

public sealed class HybridTrackingTests
{
    private static ObstacleObservation Observation(float x) => new(7, "cup", new Vector2D(x, 0), 0.2f, 0.9f, false);
    private static ObstacleTracker Tracker() => new(Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData(3000)]
    [InlineData(5000)]
    public void AcceptsDelayedSamAndRetainsStationaryGeometryAcrossPeriodicRefreshes(int age)
    {
        var tracker = Tracker();
        Assert.Equal(TrackingUpdateStatus.Accepted, tracker.Update(new ObstacleObservationBatch(1, 0, 100000, new[] { Observation(0) }), age));
        Assert.Single(tracker.Snapshot(15000, 115000).Obstacles);
        Assert.Empty(tracker.Snapshot(PerceptionTiming.VerifiedRetentionMilliseconds, 120000).Obstacles);
    }

    [Fact]
    public void MotionDoesNotRenewVerificationAndSamCorrectsIt()
    {
        var tracker = Tracker();
        tracker.Update(new ObstacleObservationBatch(1, 0, 100000, new[] { Observation(0) }), 0);
        Assert.True(tracker.ApplyLocalMotion(1, new Vector2D(0.2f, 0), 100, 100100, 0.8f, 100));
        var local = Assert.Single(tracker.Snapshot(120, 100120).Obstacles);
        Assert.Equal(ObservationSource.LocalMotionEstimate, local.ObservationSource);
        Assert.Equal(120, local.AgeMilliseconds); Assert.Equal(20, local.GeometryAgeMilliseconds);
        Assert.Equal(100000, local.LastObservedUnixMs);
        tracker.Update(new ObstacleObservationBatch(2, 150, 100150, new[] { Observation(0.3f) }), 200);
        var corrected = Assert.Single(tracker.Snapshot(200, 100200).Obstacles);
        Assert.Equal(1, corrected.Obstacle.Id); Assert.Equal(ObservationSource.SamVerified, corrected.ObservationSource);
        Assert.Equal(0.3f, corrected.Obstacle.WorldPosition.X);
        Assert.False(tracker.ApplyLocalMotion(1, default, 100, 100100, 1, 200));
    }

    [Fact]
    public void LocalHistoryAssociatesDelayedSamAtItsCaptureTime()
    {
        var tracker = Tracker();
        tracker.Update(new ObstacleObservationBatch(1, 0, 100000, new[] { Observation(0) }), 0);
        for (var time = 100; time <= 3000; time += 100)
            Assert.True(tracker.ApplyLocalMotion(1, new Vector2D(time / 1000f, 0), time, 100000 + time, 1, time));
        tracker.Update(new ObstacleObservationBatch(2, 2000, 102000, new[] { Observation(2) }), 3000);
        Assert.Equal(1, Assert.Single(tracker.Snapshot(3000, 103000).Obstacles).Obstacle.Id);
    }

    [Fact]
    public void WirePreservesProvenanceAndMovingGeometryExpiresIndependently()
    {
        var tracker = Tracker();
        tracker.Update(new ObstacleObservationBatch(1, 0, 100000, new[] { Observation(0) }), 0);
        tracker.ApplyLocalMotion(1, new Vector2D(0.1f, 0), 100, 100100, 1, 100);
        var message = new WorldStreamMessage(Guid.NewGuid(), 1, 100100, PerceptionTiming.VerifiedRetentionMilliseconds,
            tracker.Snapshot(100, 100100), null, 0);
        var decoded = WorldStreamCodec.Decode(WorldStreamCodec.Encode(message));
        Assert.Equal(ObservationSource.LocalMotionEstimate, Assert.Single(decoded.World.Obstacles).ObservationSource);
        Assert.True(WorldStreamFreshness.IsObservationCurrent(decoded, 0, 500, 100100));
        Assert.False(WorldStreamFreshness.IsObservationCurrent(decoded, 0, 1000, 100100));
        Assert.False(tracker.ApplyLocalMotion(1, new Vector2D(100, 0), 200, 100200, 1, 200));
    }
}
