using System;
using System.Collections.Generic;
using OmniTwin.Contracts;

namespace OmniTwin.Perception.Tracking;

/// <summary>The outcome of an atomic observation update.</summary>
public enum TrackingUpdateStatus
{
    /// <summary>The entire batch was accepted.</summary>
    Accepted,
    /// <summary>The source sequence or capture time is older than an accepted batch.</summary>
    OutOfOrder,
    /// <summary>The capture is future-dated or has reached the retention limit.</summary>
    StaleOrFuture,
    /// <summary>Accepting the batch would exceed the configured track capacity.</summary>
    CapacityExceeded,
    /// <summary>All available 32-bit session IDs have been consumed.</summary>
    IdentityExhausted
}

/// <summary>Thread-safe gated world-space association with bounded motion prediction and monotonic expiry.</summary>
/// <remarks>Callers must serialize logical clock values across update/publication calls. Published positions are never extrapolated.</remarks>
public sealed class ObstacleTracker
{
    private readonly object _gate = new object();
    private readonly Guid _sessionId;
    private readonly Guid _calibrationId;
    private readonly long _retentionMs;
    private readonly double _matchDistance;
    private readonly double _maximumSpeed;
    private readonly int _capacity;
    private const long TransientRetentionMilliseconds = 1500;
    private const long SettlingConfirmationMilliseconds = 750;
    private const double MotionDeadbandMeters = 0.025;
    private const double PositionEmaMaximumStepMeters = 0.15;
    private const double PositionEmaAlpha = 0.35;
    private const double RadiusEmaAlpha = 0.3;
    private const double SettledSpeedMetersPerSecond = 0.08;
    private List<Track> _tracks = new List<Track>();
    private long _nextId = 1;
    private long _publicationSequence;
    private long? _sourceSequence;
    private long _lastCapture;
    private long _lastNow = -1;

    /// <summary>Creates a session with retention, association-distance, prediction-speed, and capacity limits.</summary>
    public ObstacleTracker(Guid sessionId, Guid calibrationId, long retentionMilliseconds = PerceptionTiming.VerifiedRetentionMilliseconds,
        double maximumAssociationDistanceMeters = 1.5, double maximumPredictionSpeedMetersPerSecond = 5, int capacity = 128)
    {
        if (sessionId == Guid.Empty || calibrationId == Guid.Empty) throw new ArgumentException("Session and calibration IDs cannot be empty.");
        if (retentionMilliseconds < 1 || retentionMilliseconds > 60_000) throw new ArgumentOutOfRangeException(nameof(retentionMilliseconds));
        if (!FinitePositive(maximumAssociationDistanceMeters) || maximumAssociationDistanceMeters > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumAssociationDistanceMeters));
        if (!FinitePositive(maximumPredictionSpeedMetersPerSecond) || maximumPredictionSpeedMetersPerSecond > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumPredictionSpeedMetersPerSecond));
        if (capacity < 1 || capacity > 128) throw new ArgumentOutOfRangeException(nameof(capacity));
        _sessionId = sessionId; _calibrationId = calibrationId; _retentionMs = retentionMilliseconds;
        _matchDistance = maximumAssociationDistanceMeters; _maximumSpeed = maximumPredictionSpeedMetersPerSecond; _capacity = capacity;
    }

    /// <summary>Associates an entire batch atomically. Rejected batches leave identity and observation state unchanged.</summary>
    public TrackingUpdateStatus Update(ObstacleObservationBatch batch, long nowElapsedMilliseconds,
        ObservationSource observationSource = ObservationSource.SamVerified)
    {
        if (batch is null) throw new ArgumentNullException(nameof(batch));
        if (!Enum.IsDefined(typeof(ObservationSource), observationSource)) throw new ArgumentOutOfRangeException(nameof(observationSource));
        lock (_gate)
        {
            ValidateNow(nowElapsedMilliseconds);
            if (_sourceSequence.HasValue && (batch.SourceSequenceNumber <= _sourceSequence.Value || batch.CapturedElapsedMs < _lastCapture))
                return TrackingUpdateStatus.OutOfOrder;
            if (batch.CapturedElapsedMs > nowElapsedMilliseconds || nowElapsedMilliseconds - batch.CapturedElapsedMs >= _retentionMs)
                return TrackingUpdateStatus.StaleOrFuture;
            var observations = new List<ObstacleObservation>(batch.Observations);
            observations.Sort(CompareObservations);
            var active = _tracks.FindAll(track => IsActive(track, nowElapsedMilliseconds));
            var costs = new double[active.Count, observations.Count + active.Count];
            for (var row = 0; row < active.Count; row++)
            for (var column = 0; column < costs.GetLength(1); column++)
                costs[row, column] = column < observations.Count ? Cost(active[row], observations[column], batch.CapturedElapsedMs) : active.Count + 1;
            var assignment = MinimumCostAssignment.Solve(costs);
            var matched = new bool[observations.Count];
            var next = new List<Track>();
            for (var row = 0; row < active.Count; row++)
            {
                var column = assignment[row];
                if (column < observations.Count && costs[row, column] < 1_000_000)
                {
                    matched[column] = true;
                    next.Add(Refresh(active[row], observations[column], batch, observationSource));
                }
                else next.Add(active[row].Retain());
            }
            var newCount = 0;
            foreach (var used in matched) if (!used) newCount++;
            if (next.Count + newCount > _capacity) return TrackingUpdateStatus.CapacityExceeded;
            if (_nextId + newCount - 1 > int.MaxValue) return TrackingUpdateStatus.IdentityExhausted;
            var nextId = _nextId;
            for (var i = 0; i < observations.Count; i++)
                if (!matched[i]) next.Add(new Track((int)nextId++, observations[i], batch.CapturedElapsedMs, batch.CapturedAtUnixMs, 0, 0, true,
                    observationSource: observationSource));
            _tracks = next; _nextId = nextId; _sourceSequence = batch.SourceSequenceNumber;
            _lastCapture = batch.CapturedElapsedMs; _lastNow = nowElapsedMilliseconds;
            return TrackingUpdateStatus.Accepted;
        }
    }

    /// <summary>Resolves a response-local ID only in the latest accepted SAM batch.</summary>
    public bool TryGetVerifiedTrackId(long sourceSequenceNumber, int sourceObjectId, out int trackId, out float radiusMeters)
    {
        lock (_gate)
        {
            trackId = 0;
            radiusMeters = 0;
            if (_sourceSequence != sourceSequenceNumber) return false;
            foreach (var track in _tracks)
                if (track.Seen && track.CurrentObservationSource == ObservationSource.SamVerified && track.Observation.SourceObjectId == sourceObjectId)
                { trackId = track.Id; radiusMeters = track.SmoothedRadius; return true; }
            return false;
        }
    }

    /// <summary>Applies measured local geometry without renewing semantic verification, identity, or detector confidence.</summary>
    public bool ApplyLocalMotion(int trackId, Vector2D position, long capturedElapsedMs, long capturedAtUnixMs,
        float quality, long nowElapsedMs)
    {
        if (capturedElapsedMs < 0 || capturedAtUnixMs < 0 || float.IsNaN(quality) || quality < 0 || quality > 1)
            throw new ArgumentOutOfRangeException(nameof(capturedElapsedMs));
        lock (_gate)
        {
            ValidateNow(nowElapsedMs);
            var track = _tracks.Find(item => item.Id == trackId);
            if (track is null || capturedElapsedMs > nowElapsedMs || nowElapsedMs - capturedElapsedMs >= PerceptionTiming.LocalMotionAgeMilliseconds ||
                nowElapsedMs - track.CapturedElapsedMs >= _retentionMs || capturedElapsedMs <= (track.LocalCapture ?? track.CapturedElapsedMs)) return false;
            var seconds = (capturedElapsedMs - (track.LocalCapture ?? track.CapturedElapsedMs)) / 1000.0;
            var previousPosition = track.LocalPosition ?? track.SmoothedPosition;
            var displacement = previousPosition.DistanceTo(position);
            if (displacement > _maximumSpeed * seconds + 0.05 || quality < 0.5f) return false;
            if (seconds > 0 && displacement / seconds > 0.35)
            { track.Lifecycle = ObstacleLifecycleState.TransientManipulator; track.StableSinceElapsedMs = capturedElapsedMs; }
            else if (displacement > MotionDeadbandMeters && track.Lifecycle == ObstacleLifecycleState.SettledObstacle)
            { track.Lifecycle = ObstacleLifecycleState.Settling; track.StableSinceElapsedMs = capturedElapsedMs; }
            track.LocalPosition = Smooth(previousPosition, position, 0.55);
            track.LocalCapture = capturedElapsedMs; track.LocalUnix = capturedAtUnixMs; track.LocalQuality = quality;
            track.History.Add((capturedElapsedMs, position));
            track.History.RemoveAll(item => capturedElapsedMs - item.Time > PerceptionTiming.MaximumFrameAgeMilliseconds + 1000);
            if (track.History.Count > 512) track.History.RemoveAt(0);
            _lastNow = nowElapsedMs;
            return true;
        }
    }

    /// <summary>Expires stale tracks and creates an owned publication without moving last-observed geometry.</summary>
    public ObstacleTrackingSnapshot Snapshot(long nowElapsedMilliseconds, long timestampUnixMs)
    {
        lock (_gate)
        {
            ValidateNow(nowElapsedMilliseconds);
            if (timestampUnixMs < 0) throw new ArgumentOutOfRangeException(nameof(timestampUnixMs));
            if (_publicationSequence == long.MaxValue) throw new InvalidOperationException("Publication sequence exhausted.");
            _tracks.RemoveAll(track => !IsActive(track, nowElapsedMilliseconds));
            var states = new TrackedObstacleState[_tracks.Count];
            for (var i = 0; i < states.Length; i++)
            {
                var track = _tracks[i]; var age = nowElapsedMilliseconds - track.CapturedElapsedMs;
                var obstacle = new SegmentedObstacle(track.Id, track.Observation.Label, track.LocalPosition ?? track.SmoothedPosition,
                    track.SmoothedRadius, track.Observation.Confidence, track.Observation.IsHazardZone);
                states[i] = new TrackedObstacleState(obstacle, track.CapturedAtUnixMs, age,
                    (float)(track.Observation.Confidence * (1 - Math.Min(1, (double)age / RetentionFor(track.Lifecycle))) * track.LocalQuality), track.Seen,
                    track.LocalCapture.HasValue ? ObservationSource.LocalMotionEstimate : track.CurrentObservationSource,
                    nowElapsedMilliseconds - (track.LocalCapture ?? track.CapturedElapsedMs), track.LocalUnix ?? track.CapturedAtUnixMs,
                    track.Lifecycle);
            }
            _lastNow = nowElapsedMilliseconds;
            long? sourceAge = _sourceSequence.HasValue ? nowElapsedMilliseconds - _lastCapture : (long?)null;
            return new ObstacleTrackingSnapshot(_sessionId, _calibrationId, _publicationSequence++, _sourceSequence,
                sourceAge, sourceAge.HasValue && sourceAge.Value < _retentionMs, timestampUnixMs, states);
        }
    }

    private double Cost(Track track, ObstacleObservation observation, long capturedMs)
    {
        if (!string.Equals(track.Observation.Label, observation.Label, StringComparison.Ordinal) ||
            track.Observation.IsHazardZone != observation.IsHazardZone) return 1_000_000;
        var ratio = (double)observation.RadiusMeters / track.SmoothedRadius;
        if (ratio < 0.4 || ratio > 2.5) return 1_000_000;
        var seconds = Math.Min(1000, capturedMs - track.CapturedElapsedMs) / 1000.0;
        var predicted = track.SmoothedPosition;
        var found = false;
        for (var i = track.History.Count - 1; i >= 0; i--)
            if (track.History[i].Time <= capturedMs && capturedMs - track.History[i].Time <= 250)
            { predicted = track.History[i].Position; found = true; break; }
        var dx = observation.Position.X - ((double)predicted.X + (found ? 0 : track.Vx * seconds));
        var dz = observation.Position.Z - ((double)predicted.Z + (found ? 0 : track.Vz * seconds));
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (distance > _matchDistance) return 1_000_000;
        return 0.8 * distance / _matchDistance + 0.2 * Math.Abs(Math.Log(ratio)) / Math.Log(2.5);
    }

    private Track Refresh(Track previous, ObstacleObservation observation, ObstacleObservationBatch batch, ObservationSource observationSource)
    {
        var seconds = (batch.CapturedElapsedMs - previous.CapturedElapsedMs) / 1000.0;
        var vx = previous.Vx; var vz = previous.Vz;
        if (seconds > 0)
        {
            vx = ((double)observation.Position.X - previous.Observation.Position.X) / seconds;
            vz = ((double)observation.Position.Z - previous.Observation.Position.Z) / seconds;
            var speed = Math.Sqrt(vx * vx + vz * vz);
            if (speed > _maximumSpeed) { vx *= _maximumSpeed / speed; vz *= _maximumSpeed / speed; }
        }
        var measuredSpeed = Math.Sqrt(vx * vx + vz * vz);
        var displacement = previous.Observation.Position.DistanceTo(observation.Position);
        var manipulator = IsManipulator(observation.Label) || measuredSpeed > 0.35;
        var stableSince = measuredSpeed <= SettledSpeedMetersPerSecond && displacement <= MotionDeadbandMeters
            ? previous.StableSinceElapsedMs : batch.CapturedElapsedMs;
        var lifecycle = manipulator ? ObstacleLifecycleState.TransientManipulator :
            batch.CapturedElapsedMs - stableSince >= SettlingConfirmationMilliseconds
                ? ObstacleLifecycleState.SettledObstacle : ObstacleLifecycleState.Settling;
        var filteredPosition = displacement <= PositionEmaMaximumStepMeters
            ? Smooth(previous.SmoothedPosition, observation.Position, PositionEmaAlpha)
            : observation.Position;
        var refreshed = new Track(previous.Id, observation, batch.CapturedElapsedMs, batch.CapturedAtUnixMs, vx, vz, true,
            filteredPosition,
            Smooth(previous.SmoothedRadius, observation.RadiusMeters, RadiusEmaAlpha), stableSince, lifecycle, observationSource);
        refreshed.History.AddRange(previous.History);
        return refreshed;
    }

    private void ValidateNow(long now)
    {
        if (now < 0 || now < _lastNow) throw new ArgumentOutOfRangeException(nameof(now), "Monotonic tracker time cannot move backwards.");
    }
    private static bool FinitePositive(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
    private long RetentionFor(ObstacleLifecycleState lifecycle) => lifecycle switch
    {
        ObstacleLifecycleState.TransientManipulator => Math.Min(_retentionMs, TransientRetentionMilliseconds),
        _ => _retentionMs
    };
    private bool IsActive(Track track, long nowElapsedMs)
    {
        var observationAge = nowElapsedMs - track.CapturedElapsedMs;
        if (observationAge >= _retentionMs) return false;
        if (track.LocalCapture.HasValue && nowElapsedMs - track.LocalCapture.Value < PerceptionTiming.LocalMotionAgeMilliseconds) return true;
        return observationAge < RetentionFor(track.Lifecycle);
    }
    private static bool IsManipulator(string label) =>
        label.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0 ||
        label.IndexOf("person", StringComparison.OrdinalIgnoreCase) >= 0 ||
        label.IndexOf("finger", StringComparison.OrdinalIgnoreCase) >= 0 ||
        label.IndexOf("arm", StringComparison.OrdinalIgnoreCase) >= 0 ||
        label.IndexOf("human", StringComparison.OrdinalIgnoreCase) >= 0;
    private static Vector2D Smooth(Vector2D previous, Vector2D measured, double alpha)
    {
        if (previous.DistanceTo(measured) <= MotionDeadbandMeters) return previous;
        return new Vector2D((float)(previous.X + (measured.X - previous.X) * alpha),
            (float)(previous.Z + (measured.Z - previous.Z) * alpha));
    }
    private static float Smooth(float previous, float measured, double alpha) =>
        Math.Abs(previous - measured) <= 0.015 ? previous : (float)(previous + (measured - previous) * alpha);
    private static int CompareObservations(ObstacleObservation a, ObstacleObservation b)
    {
        var comparison = string.CompareOrdinal(a.Label, b.Label);
        if (comparison == 0) comparison = a.IsHazardZone.CompareTo(b.IsHazardZone);
        if (comparison == 0) comparison = a.Position.X.CompareTo(b.Position.X);
        if (comparison == 0) comparison = a.Position.Z.CompareTo(b.Position.Z);
        if (comparison == 0) comparison = a.RadiusMeters.CompareTo(b.RadiusMeters);
        if (comparison == 0) comparison = a.Confidence.CompareTo(b.Confidence);
        return comparison;
    }

    private sealed class Track
    {
        internal Track(int id, ObstacleObservation observation, long capture, long unix, double vx, double vz, bool seen,
            Vector2D? smoothedPosition = null, float? smoothedRadius = null, long? stableSinceElapsedMs = null,
            ObstacleLifecycleState? lifecycle = null, ObservationSource observationSource = ObservationSource.SamVerified)
        {
            Id = id; Observation = observation; CapturedElapsedMs = capture; CapturedAtUnixMs = unix; Vx = vx; Vz = vz; Seen = seen;
            SmoothedPosition = smoothedPosition ?? observation.Position; SmoothedRadius = smoothedRadius ?? observation.RadiusMeters;
            StableSinceElapsedMs = stableSinceElapsedMs ?? capture;
            Lifecycle = lifecycle ?? (IsManipulator(observation.Label) ? ObstacleLifecycleState.TransientManipulator : ObstacleLifecycleState.Settling);
            CurrentObservationSource = observationSource;
        }
        internal int Id { get; }
        internal ObstacleObservation Observation { get; }
        internal long CapturedElapsedMs { get; }
        internal long CapturedAtUnixMs { get; }
        internal double Vx { get; }
        internal double Vz { get; }
        internal bool Seen { get; }
        internal Vector2D SmoothedPosition { get; }
        internal float SmoothedRadius { get; }
        internal long StableSinceElapsedMs { get; set; }
        internal ObstacleLifecycleState Lifecycle { get; set; }
        internal ObservationSource CurrentObservationSource { get; }
        internal Vector2D? LocalPosition { get; set; }
        internal long? LocalCapture { get; set; }
        internal long? LocalUnix { get; set; }
        internal float LocalQuality { get; set; } = 1;
        internal List<(long Time, Vector2D Position)> History { get; } = new List<(long, Vector2D)>();
        internal Track Retain()
        {
            var retained = new Track(Id, Observation, CapturedElapsedMs, CapturedAtUnixMs, Vx, Vz, false,
                SmoothedPosition, SmoothedRadius, StableSinceElapsedMs, Lifecycle, CurrentObservationSource)
            { LocalPosition = LocalPosition, LocalCapture = LocalCapture, LocalUnix = LocalUnix, LocalQuality = LocalQuality };
            retained.History.AddRange(History);
            return retained;
        }
    }
}
