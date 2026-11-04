using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Perception.Calibration;
using OmniTwin.Perception.Tracking;
using Xunit;

namespace OmniTwin.Tests;

public sealed class ObstacleTrackerTests
{
    private static readonly Guid Session = Guid.Parse("86d579ea-f677-4615-bd51-8d656a5f1748");
    private static readonly Guid Calibration = Guid.Parse("445c047b-81fa-43e9-a110-c792694a7fe3");
    private static ObstacleTracker Tracker(int capacity = 128, double distance = 1.5) =>
        new(Session, Calibration, retentionMilliseconds: 3000, maximumAssociationDistanceMeters: distance, capacity: capacity);
    private static ObstacleObservation Observation(int sourceId, float x, float z = 0, float radius = 0.2f,
        string label = "cup", bool hazard = false, float confidence = 0.9f) => new(sourceId, label, new Vector2D(x, z), radius, confidence, hazard);
    private static ObstacleObservationBatch Batch(long sequence, long capture, params ObstacleObservation[] observations) =>
        new(sequence, capture, 1_000_000 + capture, observations);

    [Fact]
    public void IgnoresResponseIdsAndDetectionOrderForPersistentIdentity()
    {
        var tracker = Tracker();
        Assert.Equal(TrackingUpdateStatus.Accepted, tracker.Update(Batch(0, 0, Observation(77, 2), Observation(9, -2)), 0));
        var before = tracker.Snapshot(0, 1000);
        Assert.Equal(-2, before.Obstacles.Single(item => item.Obstacle.Id == 1).Obstacle.WorldPosition.X);
        Assert.Equal(TrackingUpdateStatus.Accepted, tracker.Update(Batch(1, 1000, Observation(9, 2.2f), Observation(77, -1.8f)), 1000));
        var after = tracker.Snapshot(1000, 2000);
        Assert.Equal(-1.8f, after.Obstacles.Single(item => item.Obstacle.Id == 1).Obstacle.WorldPosition.X);
        Assert.Equal(2.2f, after.Obstacles.Single(item => item.Obstacle.Id == 2).Obstacle.WorldPosition.X);
        Assert.Equal(-2, before.Obstacles[0].Obstacle.WorldPosition.X);
    }

    [Fact]
    public void GlobalAssociationAvoidsGreedyMatchThatWouldLoseAValidPair()
    {
        var tracker = Tracker(distance: 0.7);
        tracker.Update(Batch(0, 0, Observation(0, 0), Observation(1, 0.8f)), 0);
        tracker.Update(Batch(1, 1000, Observation(7, 0.3f), Observation(8, -0.4f)), 1000);
        var snapshot = tracker.Snapshot(1000, 2000);
        Assert.Equal(2, snapshot.Obstacles.Count);
        Assert.Equal(-0.4f, snapshot.Obstacles.Single(item => item.Obstacle.Id == 1).Obstacle.WorldPosition.X);
        Assert.Equal(0.3f, snapshot.Obstacles.Single(item => item.Obstacle.Id == 2).Obstacle.WorldPosition.X);
        Assert.All(snapshot.Obstacles, item => Assert.True(item.SeenInLatestObservation));
    }

    [Fact]
    public void ReassociatesOneMovedObjectBeyondTheNormalGateWithoutLeavingItsOldFootprint()
    {
        var tracker = new ObstacleTracker(Session, Calibration, retentionMilliseconds: 20_000,
            maximumAssociationDistanceMeters: 1.5, maximumPredictionSpeedMetersPerSecond: 5);
        tracker.Update(Batch(0, 0, Observation(0, 0)), 0);
        tracker.Update(Batch(1, 1000, Observation(7, 0)), 1000);

        // A short physical-table movement can exceed 1.5 simulation meters after calibration scaling.
        tracker.Update(Batch(2, 2000, Observation(3, 2.5f)), 2000);

        var moved = Assert.Single(tracker.Snapshot(2000, 3000).Obstacles);
        Assert.Equal(1, moved.Obstacle.Id);
        Assert.Equal(2.5f, moved.Obstacle.WorldPosition.X);
        Assert.True(moved.SeenInLatestObservation);
        Assert.Equal(ObstacleLifecycleState.TransientManipulator, moved.Lifecycle);
    }

    [Fact]
    public void DoesNotGuessWhenMultipleIdenticalObjectsCouldHaveMoved()
    {
        var tracker = new ObstacleTracker(Session, Calibration, retentionMilliseconds: 20_000,
            maximumAssociationDistanceMeters: 1.5, maximumPredictionSpeedMetersPerSecond: 5);
        tracker.Update(Batch(0, 0, Observation(0, 0), Observation(1, 0)), 0);
        tracker.Update(Batch(1, 1000, Observation(3, 0), Observation(4, 0)), 1000);
        tracker.Update(Batch(2, 2000, Observation(8, -5), Observation(9, 5)), 2000);

        var snapshot = tracker.Snapshot(2000, 3000);
        Assert.Equal(4, snapshot.Obstacles.Count);
        Assert.Equal(2, snapshot.Obstacles.Count(item => !item.SeenInLatestObservation));
        Assert.Equal(2, snapshot.Obstacles.Count(item => item.SeenInLatestObservation));
    }

    [Fact]
    public void SourceIdPermutationCannotChangeCanonicalOutputEvenForCoincidentDetections()
    {
        var first = Tracker(); var second = Tracker();
        first.Update(Batch(0, 0, Observation(0, 0, confidence: 0.4f), Observation(1, 0, confidence: 0.9f)), 0);
        second.Update(Batch(0, 0, Observation(100, 0, confidence: 0.4f), Observation(0, 0, confidence: 0.9f)), 0);
        Assert.Equal(first.Snapshot(0, 1000).Obstacles.Select(item => item.Obstacle).ToArray(),
            second.Snapshot(0, 1000).Obstacles.Select(item => item.Obstacle).ToArray());
    }

    [Fact]
    public void UsesVelocityForCrossingAssociationButNeverPublishesPredictedMotion()
    {
        var tracker = Tracker();
        tracker.Update(Batch(0, 0, Observation(0, -1.2f), Observation(1, 1.2f)), 0);
        tracker.Update(Batch(1, 1000, Observation(1, -0.4f), Observation(0, 0.4f)), 1000);
        var held = tracker.Snapshot(1500, 2500);
        Assert.Equal(-0.4f, held.Obstacles.Single(item => item.Obstacle.Id == 1).Obstacle.WorldPosition.X);
        tracker.Update(Batch(2, 2000, Observation(15, -0.4f), Observation(19, 0.4f)), 2000);
        var crossed = tracker.Snapshot(2000, 3000);
        Assert.Equal(0.4f, crossed.Obstacles.Single(item => item.Obstacle.Id == 1).Obstacle.WorldPosition.X);
        Assert.Equal(-0.4f, crossed.Obstacles.Single(item => item.Obstacle.Id == 2).Obstacle.WorldPosition.X);
    }

    [Fact]
    public void MissingObservationRetainsGeometryDecaysConfidenceAndExpiresExactlyAtLimit()
    {
        var tracker = Tracker();
        tracker.Update(Batch(0, 0, Observation(0, 2)), 0);
        tracker.Update(Batch(1, 1000), 1000);
        var retained = Assert.Single(tracker.Snapshot(1000, 2000).Obstacles);
        Assert.False(retained.SeenInLatestObservation);
        Assert.Equal(2, retained.Obstacle.WorldPosition.X);
        Assert.Equal(0.9f, retained.Obstacle.Confidence);
        Assert.InRange(retained.TrackingConfidence, 0.59999f, 0.60001f);
        Assert.Equal(1000, retained.AgeMilliseconds);
        Assert.Single(tracker.Snapshot(2999, 1).Obstacles);
        Assert.Empty(tracker.Snapshot(3000, 1).Obstacles);
        Assert.Equal(2, retained.Obstacle.WorldPosition.X);
    }

    [Fact]
    public void SmoothsSmallGeometryNoiseAndConfirmsSettledObjects()
    {
        var tracker = new ObstacleTracker(Session, Calibration, retentionMilliseconds: 20_000);
        tracker.Update(Batch(0, 0, Observation(0, 0, radius: 0.2f)), 0);
        tracker.Update(Batch(1, 1000, Observation(9, 0.1f, radius: 0.3f)), 1000);
        var settling = Assert.Single(tracker.Snapshot(1000, 2000).Obstacles);
        Assert.Equal(ObstacleLifecycleState.Settling, settling.Lifecycle);
        Assert.InRange(settling.Obstacle.WorldPosition.X, 0.034f, 0.036f);
        Assert.InRange(settling.Obstacle.RadiusMeters, 0.229f, 0.231f);

        tracker.Update(Batch(2, 2000, Observation(14, 0.1f, radius: 0.3f)), 2000);
        var settled = Assert.Single(tracker.Snapshot(2000, 3000).Obstacles);
        Assert.Equal(ObstacleLifecycleState.SettledObstacle, settled.Lifecycle);
    }

    [Fact]
    public void HandRegionsAreTransientAndExpireAfterSevenSecondsWithoutBlockingAStationaryTable()
    {
        var tracker = new ObstacleTracker(Session, Calibration, retentionMilliseconds: 20_000);
        tracker.Update(Batch(0, 0, Observation(0, 1, label: "hand")), 0);
        var hand = Assert.Single(tracker.Snapshot(0, 1000).Obstacles);
        Assert.Equal(ObstacleLifecycleState.TransientManipulator, hand.Lifecycle);
        tracker.Update(Batch(1, 1000), 1000);
        Assert.Single(tracker.Snapshot(6999, 2000).Obstacles);
        Assert.Empty(tracker.Snapshot(7000, 2000).Obstacles);
    }

    [Fact]
    public void LocalFastMotionDemotesSettledTrackToTransient()
    {
        var tracker = new ObstacleTracker(Session, Calibration, retentionMilliseconds: 20_000);
        tracker.Update(Batch(0, 0, Observation(0, 1)), 0);
        tracker.Update(Batch(1, 1000, Observation(0, 1)), 1000);
        var track = Assert.Single(tracker.Snapshot(1000, 2000).Obstacles);
        Assert.Equal(ObstacleLifecycleState.SettledObstacle, track.Lifecycle);
        Assert.True(tracker.ApplyLocalMotion(track.Obstacle.Id, new Vector2D(2, 0), 1200, 2200, 0.9f, 1200));
        Assert.Equal(ObstacleLifecycleState.TransientManipulator, Assert.Single(tracker.Snapshot(1200, 2200).Obstacles).Lifecycle);
    }

    [Fact]
    public void LocallyMovedObjectRemainsAvailableForLateSamReassociation()
    {
        var tracker = new ObstacleTracker(Session, Calibration, retentionMilliseconds: 20_000);
        tracker.Update(Batch(0, 0, Observation(0, 1, label: "cup")), 0);
        tracker.Update(Batch(1, 1000, Observation(0, 1, label: "cup")), 1000);
        var original = Assert.Single(tracker.Snapshot(1000, 2000).Obstacles);

        Assert.True(tracker.ApplyLocalMotion(original.Obstacle.Id, new Vector2D(0.8f, 1), 1200, 2200, 0.9f, 1200));
        var moved = Assert.Single(tracker.Snapshot(1200, 2200).Obstacles);
        Assert.Equal(original.Obstacle.Id, moved.Obstacle.Id);
        Assert.Equal(ObservationSource.LocalMotionEstimate, moved.ObservationSource);
        Assert.Equal(ObstacleLifecycleState.TransientManipulator, moved.Lifecycle);

        // Inference latency can be several seconds; keep the moved track for a late SAM update.
        Assert.Single(tracker.Snapshot(7999, 9000).Obstacles);
        Assert.Empty(tracker.Snapshot(8000, 9001).Obstacles);
    }

    [Fact]
    public void AgeIncludesInferenceDelayAndDoesNotUseWallClockDeltas()
    {
        var tracker = Tracker();
        tracker.Update(Batch(0, 500, Observation(1, 0)), 1500);
        var before = Assert.Single(tracker.Snapshot(1500, 5000).Obstacles);
        var after = Assert.Single(tracker.Snapshot(2000, 1000).Obstacles);
        Assert.Equal(1000, before.AgeMilliseconds);
        Assert.Equal(1500, after.AgeMilliseconds);
        Assert.True(after.TrackingConfidence < before.TrackingConfidence);
    }

    [Fact]
    public void ExpiredIdsAreNeverReusedWithinTheSession()
    {
        var tracker = Tracker();
        tracker.Update(Batch(0, 0, Observation(1, 0)), 0);
        Assert.Empty(tracker.Snapshot(3000, 4000).Obstacles);
        tracker.Update(Batch(1, 3001, Observation(1, 0)), 3001);
        var snapshot = tracker.Snapshot(3001, 4001);
        Assert.Equal(2, Assert.Single(snapshot.Obstacles).Obstacle.Id);
        Assert.Equal(Session, snapshot.SessionId);
        Assert.Equal(Calibration, snapshot.CalibrationId);
    }

    [Fact]
    public void RejectsReplayAndTimeInvalidBatchesWithoutChangingSourceState()
    {
        var tracker = Tracker();
        tracker.Update(Batch(4, 500, Observation(1, 0)), 500);
        Assert.Equal(TrackingUpdateStatus.OutOfOrder, tracker.Update(Batch(4, 600, Observation(9, 8)), 600));
        Assert.Equal(TrackingUpdateStatus.OutOfOrder, tracker.Update(Batch(5, 499), 600));
        Assert.Equal(TrackingUpdateStatus.StaleOrFuture, tracker.Update(Batch(5, 700), 600));
        Assert.Equal(TrackingUpdateStatus.StaleOrFuture, tracker.Update(Batch(5, 500), 3500));
        var unchanged = tracker.Snapshot(600, 1000);
        Assert.Equal(4, unchanged.SourceSequenceNumber);
        Assert.Equal(0, Assert.Single(unchanged.Obstacles).Obstacle.WorldPosition.X);
    }

    [Fact]
    public void CapacityFailureIsAtomicAndDoesNotConsumeIds()
    {
        var tracker = Tracker(capacity: 1);
        tracker.Update(Batch(0, 0, Observation(0, 0)), 0);
        Assert.Equal(TrackingUpdateStatus.CapacityExceeded, tracker.Update(Batch(1, 1000, Observation(0, 10)), 1000));
        var preserved = tracker.Snapshot(1000, 2000);
        Assert.Equal(0, preserved.SourceSequenceNumber);
        Assert.Equal(1, Assert.Single(preserved.Obstacles).Obstacle.Id);
        tracker.Snapshot(3000, 4000);
        Assert.Equal(TrackingUpdateStatus.Accepted, tracker.Update(Batch(1, 3001, Observation(1, 10)), 3001));
        Assert.Equal(2, Assert.Single(tracker.Snapshot(3001, 4001).Obstacles).Obstacle.Id);
    }

    [Theory]
    [InlineData("box", false, 0.2)]
    [InlineData("cup", true, 0.2)]
    [InlineData("cup", false, 1.0)]
    public void SemanticHazardAndRadiusGatesCreateNewTracks(string label, bool hazard, float radius)
    {
        var tracker = Tracker();
        tracker.Update(Batch(0, 0, Observation(0, 0)), 0);
        tracker.Update(Batch(1, 1000, Observation(0, 0, radius: radius, label: label, hazard: hazard)), 1000);
        var states = tracker.Snapshot(1000, 2000).Obstacles;
        Assert.Equal(2, states.Count);
        Assert.False(states.Single(item => item.Obstacle.Id == 1).SeenInLatestObservation);
        Assert.True(states.Single(item => item.Obstacle.Id == 2).SeenInLatestObservation);
    }

    [Fact]
    public void GuardsInputsAndOwnsBatchStorage()
    {
        var source = new List<ObstacleObservation> { Observation(0, 1) };
        var batch = new ObstacleObservationBatch(0, 0, 1000, source);
        source.Clear();
        Assert.Single(batch.Observations);
        Assert.Throws<ArgumentException>(() => Batch(0, 0, Observation(1, 0), Observation(1, 1)));
        Assert.Throws<ArgumentException>(() => new ObstacleObservationBatch(0, 0, 0, new ObstacleObservation[] { null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Batch(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObstacleTracker(Session, Calibration, maximumAssociationDistanceMeters: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObstacleTracker(Session, Calibration, retentionMilliseconds: 0));
        var tracker = Tracker();
        tracker.Update(batch, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Snapshot(99, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Update(Batch(1, 0), 99));
    }

    [Fact]
    public void ConcurrentPublicationsHaveUniqueSequencesAndOwnedState()
    {
        var tracker = Tracker();
        tracker.Update(Batch(0, 0, Observation(0, 1)), 0);
        var sequences = new ConcurrentBag<long>();
        Parallel.For(0, 100, _ => sequences.Add(tracker.Snapshot(100, 1000).SequenceNumber));
        Assert.Equal(100, sequences.Distinct().Count());
    }

    [Fact]
    public void TrackingContractsRoundTripAndEnforceOwnershipAndRanges()
    {
        var obstacle = new SegmentedObstacle(1, "cup", new Vector2D(1, 2), 0.5f, 0.8f, false);
        var state = new TrackedObstacleState(obstacle, 1000, 50, 0.7f, false);
        var states = new List<TrackedObstacleState> { state };
        var snapshot = new ObstacleTrackingSnapshot(Session, Calibration, 2, 1, 50, true, 1050, states);
        states.Clear();
        var copy = JsonSerializer.Deserialize<ObstacleTrackingSnapshot>(JsonSerializer.Serialize(snapshot));
        Assert.NotNull(copy);
        Assert.Equal(state, Assert.Single(copy.Obstacles));
        Assert.Equal(Session, copy.SessionId);
        Assert.Equal(Calibration, copy.CalibrationId);
        Assert.Equal(50, copy.SourceAgeMilliseconds);
        Assert.True(copy.IsObservationCurrent);
        Assert.Throws<NotSupportedException>(() => ((IList<TrackedObstacleState>)snapshot.Obstacles).Clear());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrackedObstacleState(obstacle, 0, 0, 0.9f, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrackedObstacleState(obstacle, 0, -1, 0.1f, true));
        Assert.Throws<ArgumentException>(() => new ObstacleTrackingSnapshot(Guid.Empty, Calibration, 0, null, null, false, 0, Array.Empty<TrackedObstacleState>()));
        Assert.Throws<ArgumentException>(() => new ObstacleTrackingSnapshot(Session, Calibration, 0, 1, 0, true, 0, new[] { state, state }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObstacleTrackingSnapshot(Session, Calibration, 0, null, null, false, 0, Array.Empty<TrackedObstacleState>(), 3));
        Assert.Throws<ArgumentException>(() => new ObstacleTrackingSnapshot(Session, Calibration, 0, 1, null, true, 0, Array.Empty<TrackedObstacleState>()));
        Assert.Throws<ArgumentException>(() => new ObstacleTrackingSnapshot(Session, Calibration, 0, null, null, true, 0, Array.Empty<TrackedObstacleState>()));
    }

    [Fact]
    public void DistinguishesAnUnobservedOrExpiredSceneFromAFreshEmptyScene()
    {
        var tracker = Tracker();
        var initial = tracker.Snapshot(0, 1000);
        Assert.False(initial.IsObservationCurrent);
        Assert.Null(initial.SourceAgeMilliseconds);
        tracker.Update(Batch(0, 1000), 1000);
        var empty = tracker.Snapshot(1100, 2100);
        Assert.Empty(empty.Obstacles);
        Assert.True(empty.IsObservationCurrent);
        Assert.Equal(100, empty.SourceAgeMilliseconds);
        var expired = tracker.Snapshot(4000, 5000);
        Assert.Empty(expired.Obstacles);
        Assert.False(expired.IsObservationCurrent);
        Assert.Equal(3000, expired.SourceAgeMilliseconds);
    }

    [Fact]
    public void WorkspaceTestsCompleteCircleAndCopiesCornersForBothOrientations()
    {
        var camera = new[] { new PixelPoint(0, 0), new PixelPoint(100, 0), new PixelPoint(100, 100), new PixelPoint(0, 100) };
        var world = new[] { new Vector2D(-5, -5), new Vector2D(5, -5), new Vector2D(5, 5), new Vector2D(-5, 5) };
        foreach (var reverse in new[] { false, true })
        {
            var workspace = new CalibratedWorkspace(reverse ? camera.Reverse().ToArray() : camera, world);
            Assert.True(workspace.ContainsPixel(0, 0));
            Assert.True(workspace.ContainsPixel(50, 50));
            Assert.False(workspace.ContainsPixel(100.001, 50));
            Assert.False(workspace.ContainsPixel(double.NaN, 50));
            Assert.True(workspace.ContainsCircle(new Vector2D(4, 0), 1));
            Assert.False(workspace.ContainsCircle(new Vector2D(4.01f, 0), 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => workspace.ContainsCircle(default, float.NaN));
        }
        var owned = new CalibratedWorkspace(camera, world);
        camera[0] = new PixelPoint(1000, 1000); world[0] = new Vector2D(1000, 1000);
        Assert.True(owned.ContainsPixel(50, 50));
        Assert.True(owned.ContainsCircle(default, 1));
    }

    [Fact]
    public void AssignmentMatchesExhaustiveOracleOnSmallRectangularMatrices()
    {
        var random = new Random(20261030);
        for (var rows = 1; rows <= 4; rows++)
        for (var columns = rows; columns <= 6; columns++)
        for (var sample = 0; sample < 10; sample++)
        {
            var costs = new double[rows, columns];
            for (var i = 0; i < rows; i++) for (var j = 0; j < columns; j++) costs[i, j] = random.NextDouble();
            var result = MinimumCostAssignment.Solve(costs);
            Assert.Equal(rows, result.Distinct().Count());
            var actual = Enumerable.Range(0, rows).Sum(row => costs[row, result[row]]);
            var expected = Exhaustive(costs, 0, new bool[columns]);
            Assert.InRange(Math.Abs(actual - expected), 0, 1e-10);
        }
        Assert.Empty(MinimumCostAssignment.Solve(new double[0, 0]));
    }

    private static double Exhaustive(double[,] costs, int row, bool[] used)
    {
        if (row == costs.GetLength(0)) return 0;
        var best = double.PositiveInfinity;
        for (var column = 0; column < used.Length; column++)
        {
            if (used[column]) continue;
            used[column] = true;
            best = Math.Min(best, costs[row, column] + Exhaustive(costs, row + 1, used));
            used[column] = false;
        }
        return best;
    }
}
