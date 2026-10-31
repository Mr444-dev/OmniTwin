using System;
using System.Collections.Generic;

namespace OmniTwin.Contracts;

/// <summary>Immutable classifier/operator memory for one stable track.</summary>
public sealed class SemanticObjectState
{
    /// <summary>Creates a safe bounded classification/status row.</summary>
    public SemanticObjectState(int trackId, string status, ObjectSemantics? classification, bool operatorOverride, string? failureCategory)
    {
        TrackId = ContractGuard.Id(trackId, nameof(trackId));
        if (status is not ("confirming" or "queued" or "classifying" or "ready" or "failed")) throw new ArgumentException("Unknown semantic state.");
        if (status == "ready" && classification is null) throw new ArgumentException("Ready requires a classification.");
        if (failureCategory is not null) { ContractGuard.Text(failureCategory, 64, nameof(failureCategory)); foreach (var c in failureCategory) if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_') throw new ArgumentException("Unsafe failure category."); }
        Status = status; Classification = classification; OperatorOverride = operatorOverride; FailureCategory = failureCategory;
    }
    /// <summary>Gets stable tracker identity.</summary>
    public int TrackId { get; }
    /// <summary>Gets worker state.</summary>
    public string Status { get; }
    /// <summary>Gets the effective result, with operator precedence.</summary>
    public ObjectSemantics? Classification { get; }
    /// <summary>Gets whether the operator currently overrides the model.</summary>
    public bool OperatorOverride { get; }
    /// <summary>Gets safe failure text.</summary>
    public string? FailureCategory { get; }
}

/// <summary>Owned session-scoped semantic memory, separate from geometry freshness.</summary>
public sealed class SemanticSceneState
{
    /// <summary>Creates an immutable bounded status publication.</summary>
    public SemanticSceneState(Guid? sessionId, IReadOnlyList<SemanticObjectState> objects)
    {
        if (sessionId == Guid.Empty || (sessionId is null && objects.Count > 0) || objects.Count > 128) throw new ArgumentException("Invalid semantic session.");
        SessionId = sessionId; Objects = ContractGuard.CopyUnique(objects, item => item.TrackId, nameof(objects));
    }
    /// <summary>Gets local schema one.</summary>
    public int SchemaVersion => 1;
    /// <summary>Gets the tracking session, or null before perception starts.</summary>
    public Guid? SessionId { get; }
    /// <summary>Gets immutable remembered tracks.</summary>
    public IReadOnlyList<SemanticObjectState> Objects { get; }
}
