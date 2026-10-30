using System;

namespace OmniTwin.Perception.Calibration;

/// <summary>A finite image coordinate in pixels, with U rightward and V downward.</summary>
public readonly struct PixelPoint
{
    /// <summary>Creates a finite image coordinate.</summary>
    public PixelPoint(double u, double v)
    {
        if (double.IsNaN(u) || double.IsInfinity(u)) throw new ArgumentOutOfRangeException(nameof(u));
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new ArgumentOutOfRangeException(nameof(v));
        U = u;
        V = v;
    }

    /// <summary>Gets horizontal pixel position.</summary>
    public double U { get; }
    /// <summary>Gets vertical pixel position.</summary>
    public double V { get; }
}
