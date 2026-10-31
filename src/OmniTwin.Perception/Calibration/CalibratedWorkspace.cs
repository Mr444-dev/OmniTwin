using System;
using System.Collections.Generic;
using OmniTwin.Contracts;

namespace OmniTwin.Perception.Calibration;

/// <summary>An immutable convex camera/world workspace and its calibrated planar mapping.</summary>
public sealed class CalibratedWorkspace
{
    private readonly PixelPoint[] _camera;
    private readonly Vector2D[] _world;
    private readonly double _cameraSign;
    private readonly double _worldSign;

    /// <summary>Copies corresponding perimeter-ordered corners and rejects invalid homographies or polygons.</summary>
    public CalibratedWorkspace(IReadOnlyList<PixelPoint> cameraCorners, IReadOnlyList<Vector2D> worldCorners)
    {
        if (cameraCorners is null) throw new ArgumentNullException(nameof(cameraCorners));
        if (worldCorners is null) throw new ArgumentNullException(nameof(worldCorners));
        if (cameraCorners.Count != 4 || worldCorners.Count != 4) throw new ArgumentException("Exactly four corners are required.");
        _camera = new PixelPoint[4]; _world = new Vector2D[4];
        for (var i = 0; i < 4; i++) { _camera[i] = cameraCorners[i]; _world[i] = worldCorners[i]; }
        Mapper = new HomographyMapper(_camera, _world);
        _cameraSign = Math.Sign(Cross(_camera[0].U, _camera[0].V, _camera[1].U, _camera[1].V, _camera[2].U, _camera[2].V));
        _worldSign = Math.Sign(Cross(_world[0].X, _world[0].Z, _world[1].X, _world[1].Z, _world[2].X, _world[2].Z));
    }

    /// <summary>Gets the immutable mapper for this workspace.</summary>
    public IHomographyMapper Mapper { get; }
    /// <summary>Gets a read-only view of the owned perimeter-ordered world corners.</summary>
    public IReadOnlyList<Vector2D> WorldCorners => Array.AsReadOnly(_world);

    /// <summary>Tests whether a finite pixel center lies inside or on the calibrated camera polygon.</summary>
    public bool ContainsPixel(double u, double v)
    {
        if (double.IsNaN(u) || double.IsInfinity(u) || double.IsNaN(v) || double.IsInfinity(v)) return false;
        for (var i = 0; i < 4; i++)
        {
            var a = _camera[i]; var b = _camera[(i + 1) % 4];
            if (_cameraSign * Cross(a.U, a.V, b.U, b.V, u, v) < 0) return false;
        }
        return true;
    }

    /// <summary>Tests whether the entire positive-radius circle fits within all world polygon edges.</summary>
    public bool ContainsCircle(Vector2D center, float radiusMeters)
    {
        if (float.IsNaN(radiusMeters) || float.IsInfinity(radiusMeters) || radiusMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(radiusMeters));
        for (var i = 0; i < 4; i++)
        {
            var a = _world[i]; var b = _world[(i + 1) % 4];
            var dx = (double)b.X - a.X; var dz = (double)b.Z - a.Z;
            var distance = _worldSign * Cross(a.X, a.Z, b.X, b.Z, center.X, center.Z) / Math.Sqrt(dx * dx + dz * dz);
            if (distance < radiusMeters) return false;
        }
        return true;
    }

    private static double Cross(double ax, double ay, double bx, double by, double x, double y) =>
        (bx - ax) * (y - ay) - (by - ay) * (x - ax);
}
