using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using OmniTwin.Contracts;
using OmniTwin.Perception.Models;
using OmniTwin.Perception.Tracking;
using OpenCvSharp;

namespace OmniTwin.Backend.Media;

/// <summary>Session-scoped two-observation confirmation, bounded asynchronous ROI classification, and explicit operator precedence.</summary>
public sealed class ObjectSemanticService : BackgroundService
{
    private readonly object _gate = new();
    private readonly IObjectSemanticClient _client;
    private readonly Channel<Job> _jobs = Channel.CreateBounded<Job>(new BoundedChannelOptions(8) { SingleReader = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<int, Entry> _objects = new();
    private Guid? _session;
    private CancellationTokenSource _sessionStop = new();
    private bool _disposed;
    private sealed class Entry
    {
        internal int Confirmations;
        internal long Sequence = -1, Captured;
        internal string Status = "confirming";
        internal string? Failure;
        internal ObjectSemantics? Model, Manual;
    }
    private sealed record Job(Guid Session, int TrackId, VerifiedMaskBatch Batch, CancellationToken SessionToken);
    /// <summary>Creates the bounded worker with a real environment-backed model adapter.</summary>
    public ObjectSemanticService(IObjectSemanticClient client) => _client = client;
    /// <summary>Gets current identity without allocating a status snapshot on the camera path.</summary>
    public Guid? SessionId { get { lock (_gate) return _session; } }
    /// <summary>Records accepted SAM identities without doing image encoding, HTTP, or waiting on the publication path.</summary>
    public void Observe(VerifiedMaskBatch batch)
    {
        lock (_gate)
        {
            if (_session != batch.SessionId) { EndCore(); _session = batch.SessionId; }
            foreach (var pair in batch.Masks)
            {
                if (!_objects.TryGetValue(pair.Key, out var entry))
                {
                    if (_objects.Count >= 128) continue;
                    _objects.Add(pair.Key, entry = new Entry());
                }
                if (entry.Sequence >= batch.Result.SequenceNumber) continue;
                var first = entry.Confirmations == 0;
                entry.Sequence = batch.Result.SequenceNumber; entry.Confirmations++;
                if (first) entry.Captured = batch.Result.CapturedAt.ToUnixTimeMilliseconds();
                if (entry.Status != "confirming" || entry.Confirmations < 2 || pair.Value.Confidence < 0.6f || batch.Result.CapturedAt.ToUnixTimeMilliseconds() - entry.Captured < 500) continue;
                if (_jobs.Writer.TryWrite(new Job(batch.SessionId, pair.Key, batch, _sessionStop.Token))) entry.Status = "queued";
                else entry.Failure = "semantic_queue_full";
            }
        }
    }
    /// <summary>Returns an owned classification snapshot without images, prompts, or raw upstream responses.</summary>
    public SemanticSceneState Read()
    {
        lock (_gate) return new SemanticSceneState(_session, _objects.OrderBy(pair => pair.Key)
            .Select(pair => new SemanticObjectState(pair.Key, pair.Value.Status,
                pair.Value.Manual ?? pair.Value.Model, pair.Value.Manual is not null, pair.Value.Failure)).ToArray());
    }
    /// <summary>Sets or clears an operator override only in the current tracking session and existing stable ID.</summary>
    public bool Override(Guid session, int trackId, ObjectSemantics? value)
    {
        lock (_gate)
        {
            if (_session != session || !_objects.TryGetValue(trackId, out var entry)) return false;
            entry.Manual = value; return true;
        }
    }
    /// <summary>Gets an effective semantic tag, without changing verification ages.</summary>
    public ObjectSemantics? Resolve(int trackId) { lock (_gate) return _objects.TryGetValue(trackId, out var entry) ? entry.Manual ?? entry.Model : null; }
    /// <summary>Resolves hazard coloring with the same operator precedence used by geometry decoration.</summary>
    public bool IsHazard(int trackId, bool initialHazard)
    { lock (_gate) return _objects.TryGetValue(trackId, out var entry) ? entry.Manual?.IsHazardZone ?? (initialHazard || entry.Model?.IsHazardZone == true) : initialHazard; }
    /// <summary>Decorates actual tracked obstacles; classification never creates geometry or renews source freshness.</summary>
    public ObstacleTrackingSnapshot Decorate(ObstacleTrackingSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_session != snapshot.SessionId) return snapshot;
            var live = new HashSet<int>(snapshot.Obstacles.Select(track => track.Obstacle.Id));
            foreach (var id in _objects.Keys.Where(id => !live.Contains(id)).ToArray()) _objects.Remove(id);
            var tracks = snapshot.Obstacles.Select(track =>
            {
                var original = track.Obstacle; var tag = Resolve(original.Id);
                if (tag is null) return track;
                var manual = _objects[original.Id].Manual is not null;
                var obstacle = new SegmentedObstacle(original.Id, tag.Label, original.WorldPosition, original.RadiusMeters, original.Confidence,
                    manual ? tag.IsHazardZone : original.IsHazardZone || tag.IsHazardZone);
                return new TrackedObstacleState(obstacle, track.LastObservedUnixMs, track.AgeMilliseconds, track.TrackingConfidence,
                    track.SeenInLatestObservation, track.ObservationSource, track.GeometryAgeMilliseconds, track.GeometryObservedUnixMs);
            }).ToArray();
            return new ObstacleTrackingSnapshot(snapshot.SessionId, snapshot.CalibrationId, snapshot.SequenceNumber, snapshot.SourceSequenceNumber,
                snapshot.SourceAgeMilliseconds, snapshot.IsObservationCurrent, snapshot.TimestampUnixMs, tracks);
        }
    }
    /// <summary>Cancels a camera session and clears its memory. A late model reply cannot affect the next session.</summary>
    public void EndSession() { lock (_gate) EndCore(); }
    private void EndCore() { _sessionStop.Cancel(); _sessionStop.Dispose(); _sessionStop = new CancellationTokenSource(); _session = null; _objects.Clear(); while (_jobs.Reader.TryRead(out _)) { } }
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (_session != job.Session || !_objects.TryGetValue(job.TrackId, out var entry)) continue;
                entry.Status = "classifying"; entry.Failure = null;
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.SessionToken);
            byte[]? crop = null;
            try
            {
                stop.Token.ThrowIfCancellationRequested(); crop = job.Batch.Crop(job.TrackId);
                var result = await _client.ClassifyAsync(crop, stop.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_session != job.Session || stop.IsCancellationRequested || !_objects.TryGetValue(job.TrackId, out var entry)) continue;
                    entry.Model = result.Confidence >= 0.65f ? result : new ObjectSemantics("Uncertain object", "unknown", "unknown", result.Confidence);
                    entry.Status = "ready"; entry.Failure = null;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { if (stoppingToken.IsCancellationRequested) return; }
            catch (Exception error) when (error is ModelApiException or OpenCVException or ArgumentException or InvalidOperationException)
            {
                lock (_gate) if (_session == job.Session && _objects.TryGetValue(job.TrackId, out var entry))
                { entry.Status = "failed"; entry.Failure = error is ModelApiException api ? api.Category : "semantic_crop_failed"; }
            }
            finally { if (crop is not null) Array.Clear(crop); }
        }
    }
    /// <inheritdoc />
    public override void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; base.Dispose(); _sessionStop.Cancel(); _sessionStop.Dispose(); } }
}
