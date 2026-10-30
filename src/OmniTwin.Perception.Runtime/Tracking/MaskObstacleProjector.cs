using System;
using System.Collections.Generic;
using System.IO;
using OmniTwin.Perception.Vision;

namespace OmniTwin.Perception.Tracking;

/// <summary>Why a detection was excluded from a world observation batch.</summary>
public enum ProjectionRejectionReason
{
    /// <summary>Detector confidence is below the configured threshold.</summary>
    LowConfidence,
    /// <summary>No foreground pixel was present.</summary>
    EmptyMask,
    /// <summary>The estimated base span falls outside the calibrated camera polygon.</summary>
    OutsideCameraWorkspace,
    /// <summary>The base cannot be mapped to finite world coordinates.</summary>
    InvalidProjection,
    /// <summary>The estimated radius including clearance exceeds its limit.</summary>
    RadiusLimit,
    /// <summary>The complete estimated circle does not fit inside the world workspace.</summary>
    OutsideWorldWorkspace
}

/// <summary>A response-local detection excluded from projection, with an explicit reason.</summary>
public sealed class ProjectionRejection
{
    internal ProjectionRejection(int sourceObjectId, ProjectionRejectionReason reason) { SourceObjectId = sourceObjectId; Reason = reason; }
    /// <summary>Gets the response-local detection ID.</summary>
    public int SourceObjectId { get; }
    /// <summary>Gets the rejection reason.</summary>
    public ProjectionRejectionReason Reason { get; }
}

/// <summary>An owned set of accepted world observations and per-detection projection diagnostics.</summary>
public sealed class ObstacleProjection
{
    internal ObstacleProjection(List<ObstacleObservation> observations, List<ProjectionRejection> rejections)
    { Observations = observations.AsReadOnly(); Rejections = rejections.AsReadOnly(); }
    /// <summary>Gets observations safe to pass through the configured workspace checks.</summary>
    public IReadOnlyList<ObstacleObservation> Observations { get; }
    /// <summary>Gets excluded detections.</summary>
    public IReadOnlyList<ProjectionRejection> Rejections { get; }
}

/// <summary>Estimates a base contact span from the lowest occupied 10% mask band and maps it onto the calibrated plane.</summary>
/// <remarks>This is a monocular footprint heuristic, not a reconstruction of object depth or a physical safety guarantee.</remarks>
public sealed class MaskObstacleProjector
{
    private readonly CalibrationProfile _profile;
    /// <summary>Creates a stateless projector for an immutable calibration profile.</summary>
    public MaskObstacleProjector(CalibrationProfile profile) => _profile = profile ?? throw new ArgumentNullException(nameof(profile));

    /// <summary>Projects one image result; image dimensions must match calibration exactly.</summary>
    public ObstacleProjection Project(SamSegmentationResult result, string label, bool isHazardZone = false)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        if (string.IsNullOrWhiteSpace(label) || label.Length > 128) throw new ArgumentException("A label of at most 128 characters is required.", nameof(label));
        if (result.Width != _profile.ImageWidth || result.Height != _profile.ImageHeight)
            throw new InvalidDataException("Image dimensions do not match calibration; recalibrate before using this stream.");
        var observations = new List<ObstacleObservation>(); var rejections = new List<ProjectionRejection>();
        foreach (var detection in result.Detections)
        {
            var reason = ProjectOne(detection, label, isHazardZone, out var observation);
            if (reason.HasValue) rejections.Add(new ProjectionRejection(detection.ObjectId, reason.Value));
            else observations.Add(observation!);
        }
        return new ObstacleProjection(observations, rejections);
    }

    private ProjectionRejectionReason? ProjectOne(SamDetection detection, string label, bool hazard, out ObstacleObservation? observation)
    {
        observation = null;
        if (detection.Confidence < _profile.MinimumConfidence) return ProjectionRejectionReason.LowConfidence;
        if (detection.ForegroundPixelCount == 0) return ProjectionRejectionReason.EmptyMask;
        var bottom = detection.MaskHeight - 1;
        for (; bottom >= 0; bottom--)
        {
            var occupied = false;
            for (var x = 0; x < detection.MaskWidth; x++) if (detection.IsForeground(x, bottom)) { occupied = true; break; }
            if (occupied) break;
        }
        if (bottom < 0) return ProjectionRejectionReason.EmptyMask;
        var left = detection.MaskWidth; var right = -1;
        var firstRow = Math.Max(0, bottom - Math.Max(1, (detection.MaskHeight + 9) / 10) + 1);
        for (var y = firstRow; y <= bottom; y++)
        for (var x = 0; x < detection.MaskWidth; x++)
            if (detection.IsForeground(x, y)) { left = Math.Min(left, x); right = Math.Max(right, x); }
        var scaleX = (detection.Box.Right - detection.Box.Left + 1.0) / detection.MaskWidth;
        var scaleY = (detection.Box.Bottom - detection.Box.Top + 1.0) / detection.MaskHeight;
        var uLeft = detection.Box.Left + left * scaleX - 0.5;
        var uRight = detection.Box.Left + (right + 1) * scaleX - 0.5;
        var v = detection.Box.Top + (bottom + 1.0) * scaleY - 0.5;
        var u = (uLeft + uRight) / 2;
        var workspace = _profile.Workspace;
        if (!workspace.ContainsPixel(uLeft, v) || !workspace.ContainsPixel(uRight, v))
            return ProjectionRejectionReason.OutsideCameraWorkspace;
        if (!workspace.Mapper.TryPixelToWorld(u, v, out var center) || !workspace.Mapper.TryPixelToWorld(uLeft, v, out var edgeLeft) ||
            !workspace.Mapper.TryPixelToWorld(uRight, v, out var edgeRight)) return ProjectionRejectionReason.InvalidProjection;
        var radius = Math.Max(_profile.MinimumRadiusMeters, Math.Max(center.DistanceTo(edgeLeft), center.DistanceTo(edgeRight))) + _profile.SafetyMarginMeters;
        if (!double.IsFinite(radius) || radius > _profile.MaximumRadiusMeters) return ProjectionRejectionReason.RadiusLimit;
        if (!workspace.ContainsCircle(center, (float)radius)) return ProjectionRejectionReason.OutsideWorldWorkspace;
        observation = new ObstacleObservation(detection.ObjectId, label, center, (float)radius, detection.Confidence, hazard);
        return null;
    }
}
