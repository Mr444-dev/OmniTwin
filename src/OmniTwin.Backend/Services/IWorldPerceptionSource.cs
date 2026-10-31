using System;
using System.Collections.Generic;
using System.Threading;
using OmniTwin.Contracts;

namespace OmniTwin.Backend.Services;

/// <summary>An immutable tracking publication entering the backend composition layer.</summary>
public sealed class PerceptionPublication
{
    /// <summary>Creates a publication whose envelope-level diagnostics are validated before serialization.</summary>
    public PerceptionPublication(ObstacleTrackingSnapshot snapshot, string? failureCategory, int rejectedDetectionCount,
        PerceptionSourceMode sourceMode = PerceptionSourceMode.LiveCamera)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        if (failureCategory is not null && (string.IsNullOrWhiteSpace(failureCategory) || failureCategory.Length > 128))
            throw new ArgumentException("Invalid failure category.", nameof(failureCategory));
        if (rejectedDetectionCount < 0 || rejectedDetectionCount > 128) throw new ArgumentOutOfRangeException(nameof(rejectedDetectionCount));
        if (!Enum.IsDefined(typeof(PerceptionSourceMode), sourceMode)) throw new ArgumentOutOfRangeException(nameof(sourceMode));
        FailureCategory = failureCategory; RejectedDetectionCount = rejectedDetectionCount; SourceMode = sourceMode;
    }
    /// <summary>Gets the shared tracking snapshot.</summary>
    public ObstacleTrackingSnapshot Snapshot { get; }
    /// <summary>Gets sanitized perception failure state.</summary>
    public string? FailureCategory { get; }
    /// <summary>Gets the latest projection rejection count.</summary>
    public int RejectedDetectionCount { get; }
    /// <summary>Gets an explicit live or synthetic provenance label.</summary>
    public PerceptionSourceMode SourceMode { get; }
}

/// <summary>Produces real perception publications without coupling the transport to camera/model APIs.</summary>
public interface IWorldPerceptionSource
{
    /// <summary>Gets whether this source was explicitly enabled.</summary>
    bool IsEnabled { get; }
    /// <summary>Gets the source identity propagated into every HUD-visible stream packet.</summary>
    PerceptionSourceMode SourceMode { get; }
    /// <summary>Runs one owned perception session until completion, failure, or cancellation.</summary>
    IAsyncEnumerable<PerceptionPublication> ReadAllAsync(CancellationToken cancellationToken = default);
}
