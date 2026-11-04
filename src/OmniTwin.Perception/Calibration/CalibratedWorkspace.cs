using System;
using System.Collections.Generic;
using OmniTwin.Contracts;

namespace OmniTwin.Perception.Calibration;

/// <summary>An immutable convex camera/world workspace and its calibrated planar mapping.</summary>
public sealed class CalibratedWorkspace
{
    private readonly PixelPoint[] _camera;
    private readonly PixelPoint[] _cameraBoundary;
    private readonly Vector2D[] _world;
    private readonly double _worldSign;

    /// <summary>Copies the homography corners and optional perimeter boundary, rejecting invalid geometry.</summary>
    public CalibratedWorkspace(IReadOnlyList<PixelPoint> cameraCorners, IReadOnlyList<Vector2D> worldCorners,
        IReadOnlyList<PixelPoint>? cameraBoundary = null)
    {
        if (cameraCorners is null) throw new ArgumentNullException(nameof(cameraCorners));
        if (worldCorners is null) throw new ArgumentNullException(nameof(worldCorners));
        if (cameraCorners.Count != 4 || worldCorners.Count != 4) throw new ArgumentException("Exactly four corners are required.");
        _camera = new PixelPoint[4]; _world = new Vector2D[4];
        for (var i = 0; i < 4; i++) { _camera[i] = cameraCorners[i]; _world[i] = worldCorners[i]; }
        var boundary = cameraBoundary ?? cameraCorners;
        if (boundary.Count is < 4 or > 64) throw new ArgumentException("The camera boundary must contain 4 to 64 points.", nameof(cameraBoundary));
        _cameraBoundary = new PixelPoint[boundary.Count];
        for (var i = 0; i < boundary.Count; i++) _cameraBoundary[i] = boundary[i];
        ValidateSimplePolygon(_cameraBoundary, nameof(cameraBoundary));
        Mapper = new HomographyMapper(_camera, _world);
        _worldSign = Math.Sign(Cross(_world[0].X, _world[0].Z, _world[1].X, _world[1].Z, _world[2].X, _world[2].Z));
    }

    /// <summary>Gets the immutable mapper for this workspace.</summary>
    public IHomographyMapper Mapper { get; }
    /// <summary>Gets a read-only view of the owned perimeter-ordered world corners.</summary>
    public IReadOnlyList<Vector2D> WorldCorners => Array.AsReadOnly(_world);
    /// <summary>Gets a read-only view of the owned perimeter-ordered camera boundary.</summary>
    public IReadOnlyList<PixelPoint> CameraBoundary => Array.AsReadOnly(_cameraBoundary);

    /// <summary>Tests whether a finite pixel center lies inside or on the possibly concave camera boundary.</summary>
    public bool ContainsPixel(double u, double v)
    {
        if (double.IsNaN(u) || double.IsInfinity(u) || double.IsNaN(v) || double.IsInfinity(v)) return false;
        var inside = false;
        for (var i = 0; i < _cameraBoundary.Length; i++)
        {
            var a = _cameraBoundary[i]; var b = _cameraBoundary[(i + 1) % _cameraBoundary.Length];
            if (OnSegment(a.U, a.V, b.U, b.V, u, v)) return true;
            if ((a.V > v) != (b.V > v) && u < (b.U - a.U) * (v - a.V) / (b.V - a.V) + a.U)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Tests whether an entire horizontal pixel span lies inside or on the camera boundary.</summary>
    public bool ContainsPixelSpan(double leftU, double rightU, double v)
    {
        if (!double.IsFinite(leftU) || !double.IsFinite(rightU) || !double.IsFinite(v)) return false;
        if (leftU > rightU) (leftU, rightU) = (rightU, leftU);
        if (!ContainsPixel(leftU, v) || !ContainsPixel(rightU, v)) return false;

        Span<double> breakpoints = stackalloc double[66];
        var count = 0;
        breakpoints[count++] = leftU;
        breakpoints[count++] = rightU;
        for (var i = 0; i < _cameraBoundary.Length; i++)
        {
            var a = _cameraBoundary[i]; var b = _cameraBoundary[(i + 1) % _cameraBoundary.Length];
            if ((a.V > v) == (b.V > v)) continue;
            var intersectionU = a.U + (v - a.V) * (b.U - a.U) / (b.V - a.V);
            if (intersectionU > leftU && intersectionU < rightU) breakpoints[count++] = intersectionU;
        }

        for (var i = 1; i < count; i++)
        {
            var value = breakpoints[i];
            var j = i - 1;
            while (j >= 0 && breakpoints[j] > value)
            {
                breakpoints[j + 1] = breakpoints[j];
                j--;
            }
            breakpoints[j + 1] = value;
        }
        for (var i = 0; i < count - 1; i++)
        {
            var start = breakpoints[i]; var end = breakpoints[i + 1];
            if (end - start > 1e-9 && !ContainsPixel((start + end) / 2, v)) return false;
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

    private static bool OnSegment(double ax, double ay, double bx, double by, double x, double y)
    {
        var cross = Cross(ax, ay, bx, by, x, y);
        var scale = Math.Max(1, Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)));
        return Math.Abs(cross) <= 1e-9 * scale && x >= Math.Min(ax, bx) - 1e-9 && x <= Math.Max(ax, bx) + 1e-9 &&
            y >= Math.Min(ay, by) - 1e-9 && y <= Math.Max(ay, by) + 1e-9;
    }

    private static void ValidateSimplePolygon(IReadOnlyList<PixelPoint> polygon, string parameterName)
    {
        double twiceArea = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            if (a.U == b.U && a.V == b.V) throw new ArgumentException("The camera boundary cannot contain duplicate neighboring points.", parameterName);
            twiceArea += a.U * b.V - b.U * a.V;
            var previous = polygon[(i + polygon.Count - 1) % polygon.Count];
            var turn = Cross(previous.U, previous.V, a.U, a.V, b.U, b.V);
            var incomingU = a.U - previous.U; var incomingV = a.V - previous.V;
            var outgoingU = b.U - a.U; var outgoingV = b.V - a.V;
            if (Math.Abs(turn) < 1e-9 && incomingU * outgoingU + incomingV * outgoingV < 0)
                throw new ArgumentException("The camera boundary cannot double back along an edge.", parameterName);
        }
        if (Math.Abs(twiceArea) < 1e-9) throw new ArgumentException("The camera boundary must enclose a nonzero area.", parameterName);

        for (var first = 0; first < polygon.Count; first++)
        {
            var firstNext = (first + 1) % polygon.Count;
            for (var second = first + 1; second < polygon.Count; second++)
            {
                var secondNext = (second + 1) % polygon.Count;
                if (first == second || firstNext == second || secondNext == first) continue;
                if (SegmentsIntersect(polygon[first], polygon[firstNext], polygon[second], polygon[secondNext]))
                    throw new ArgumentException("The camera boundary cannot cross itself.", parameterName);
            }
        }
    }

    private static bool SegmentsIntersect(PixelPoint a, PixelPoint b, PixelPoint c, PixelPoint d)
    {
        var abC = Cross(a.U, a.V, b.U, b.V, c.U, c.V);
        var abD = Cross(a.U, a.V, b.U, b.V, d.U, d.V);
        var cdA = Cross(c.U, c.V, d.U, d.V, a.U, a.V);
        var cdB = Cross(c.U, c.V, d.U, d.V, b.U, b.V);
        if (((abC > 0 && abD < 0) || (abC < 0 && abD > 0)) && ((cdA > 0 && cdB < 0) || (cdA < 0 && cdB > 0))) return true;
        return abC == 0 && OnSegment(a.U, a.V, b.U, b.V, c.U, c.V) ||
            abD == 0 && OnSegment(a.U, a.V, b.U, b.V, d.U, d.V) ||
            cdA == 0 && OnSegment(c.U, c.V, d.U, d.V, a.U, a.V) ||
            cdB == 0 && OnSegment(c.U, c.V, d.U, d.V, b.U, b.V);
    }
}
