using OmniTwin.Contracts;

namespace OmniTwin.Perception.Calibration;

/// <summary>Projects image points onto one calibrated world plane; implementations must support concurrent reads.</summary>
public interface IHomographyMapper
{
    /// <summary>Maps a point to world meters, throwing if input or projection is invalid.</summary>
    /// <remarks>Points outside the calibration polygon are extrapolated; ingestion must enforce workspace bounds.</remarks>
    Vector2D PixelToWorld(double u, double v);

    /// <summary>Returns false for nonfinite inputs, near-horizon projections, or nonrepresentable world values.</summary>
    /// <remarks>When false, the out value must be ignored; it is not a valid observation.</remarks>
    bool TryPixelToWorld(double u, double v, out Vector2D worldPosition);
}
