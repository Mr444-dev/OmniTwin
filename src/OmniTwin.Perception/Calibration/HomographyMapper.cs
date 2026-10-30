using System;
using System.Collections.Generic;
using OmniTwin.Contracts;

namespace OmniTwin.Perception.Calibration;

/// <summary>An immutable, normalized four-correspondence planar homography.</summary>
/// <remarks>
/// Input polygons must be strictly convex and supplied in perimeter order with matching physical corners.
/// Coordinates are centered and scaled before an 8-by-9 homogeneous system is solved with complete pivoting.
/// Mapping performs no I/O, locking, or heap allocation and can be called concurrently.
/// </remarks>
public sealed class HomographyMapper : IHomographyMapper
{
    private const double GeometryTolerance = 1e-8;
    private const double NumericalTolerance = 1e-12;
    private readonly double[] _matrix;
    private readonly Normalization _source;
    private readonly Normalization _destination;

    /// <summary>Calibrates from exactly four corresponding camera pixels and world positions in meters.</summary>
    /// <exception cref="ArgumentException">Corners are nonconvex, degenerate, ill-conditioned, or inconsistent.</exception>
    /// <exception cref="ArgumentNullException">An input collection is null.</exception>
    public HomographyMapper(IReadOnlyList<PixelPoint> cameraCorners, IReadOnlyList<Vector2D> worldCorners)
    {
        if (cameraCorners is null) throw new ArgumentNullException(nameof(cameraCorners));
        if (worldCorners is null) throw new ArgumentNullException(nameof(worldCorners));
        if (cameraCorners.Count != 4 || worldCorners.Count != 4)
            throw new ArgumentException("Calibration requires exactly four corresponding corners.");

        var source = new PixelPoint[4];
        var destination = new PixelPoint[4];
        for (var i = 0; i < 4; i++)
        {
            source[i] = cameraCorners[i];
            destination[i] = new PixelPoint(worldCorners[i].X, worldCorners[i].Z);
        }

        _source = Normalization.Create(source, nameof(cameraCorners));
        _destination = Normalization.Create(destination, nameof(worldCorners));
        var normalizedSource = Normalize(source, _source);
        var normalizedDestination = Normalize(destination, _destination);
        ValidatePolygon(normalizedSource, nameof(cameraCorners));
        ValidatePolygon(normalizedDestination, nameof(worldCorners));
        _matrix = Solve(normalizedSource, normalizedDestination);
        ValidateFit(normalizedSource, normalizedDestination);
    }

    /// <inheritdoc />
    public Vector2D PixelToWorld(double u, double v)
    {
        if (!IsFinite(u)) throw new ArgumentOutOfRangeException(nameof(u), "Pixel coordinate must be finite.");
        if (!IsFinite(v)) throw new ArgumentOutOfRangeException(nameof(v), "Pixel coordinate must be finite.");
        if (!TryPixelToWorld(u, v, out var result))
            throw new InvalidOperationException("Projection is near the projective horizon or outside the finite world range.");
        return result;
    }

    /// <inheritdoc />
    public bool TryPixelToWorld(double u, double v, out Vector2D worldPosition)
    {
        worldPosition = default;
        if (!IsFinite(u) || !IsFinite(v)) return false;
        var normalizedU = (u - _source.CenterU) * _source.Scale;
        var normalizedV = (v - _source.CenterV) * _source.Scale;
        if (!TryProject(normalizedU, normalizedV, out var x, out var z)) return false;
        x = x / _destination.Scale + _destination.CenterU;
        z = z / _destination.Scale + _destination.CenterV;
        if (!IsFinite(x) || !IsFinite(z) || Math.Abs(x) > float.MaxValue || Math.Abs(z) > float.MaxValue)
            return false;
        worldPosition = new Vector2D((float)x, (float)z);
        return true;
    }

    private bool TryProject(double u, double v, out double x, out double z)
    {
        x = z = 0;
        if (!IsFinite(u) || !IsFinite(v)) return false;
        var termU = _matrix[6] * u;
        var termV = _matrix[7] * v;
        var denominator = termU + termV + _matrix[8];
        var scale = Math.Max(1, Math.Abs(termU) + Math.Abs(termV) + Math.Abs(_matrix[8]));
        if (!IsFinite(denominator) || !IsFinite(scale) || Math.Abs(denominator) <= NumericalTolerance * scale)
            return false;
        x = (_matrix[0] * u + _matrix[1] * v + _matrix[2]) / denominator;
        z = (_matrix[3] * u + _matrix[4] * v + _matrix[5]) / denominator;
        return IsFinite(x) && IsFinite(z);
    }

    private void ValidateFit(PixelPoint[] source, PixelPoint[] destination)
    {
        var previousSign = 0;
        for (var i = 0; i < 4; i++)
        {
            if (!TryProject(source[i].U, source[i].V, out var x, out var z) ||
                Math.Abs(x - destination[i].U) > 1e-7 || Math.Abs(z - destination[i].V) > 1e-7)
                throw new ArgumentException("Calibration is numerically unstable; choose better separated corners.");
            var denominator = _matrix[6] * source[i].U + _matrix[7] * source[i].V + _matrix[8];
            var sign = Math.Sign(denominator);
            if (previousSign != 0 && sign != previousSign)
                throw new ArgumentException("Calibration crosses the projective horizon inside the workspace.");
            previousSign = sign;
        }
    }

    private static PixelPoint[] Normalize(PixelPoint[] points, Normalization normalization)
    {
        var result = new PixelPoint[4];
        for (var i = 0; i < 4; i++)
            result[i] = new PixelPoint((points[i].U - normalization.CenterU) * normalization.Scale,
                (points[i].V - normalization.CenterV) * normalization.Scale);
        return result;
    }

    private static void ValidatePolygon(PixelPoint[] points, string parameterName)
    {
        var previousSign = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % 4];
            var c = points[(i + 2) % 4];
            var cross = (b.U - a.U) * (c.V - b.V) - (b.V - a.V) * (c.U - b.U);
            if (Math.Abs(cross) <= GeometryTolerance)
                throw new ArgumentException("Corners are duplicate, collinear, or too poorly separated.", parameterName);
            var sign = Math.Sign(cross);
            if (previousSign != 0 && sign != previousSign)
                throw new ArgumentException("Corners must form a strictly convex polygon in perimeter order.", parameterName);
            previousSign = sign;
        }
    }

    private static double[] Solve(PixelPoint[] source, PixelPoint[] destination)
    {
        var system = new double[8, 9];
        var permutation = new int[9];
        for (var i = 0; i < 9; i++) permutation[i] = i;
        var maximum = 0.0;
        for (var i = 0; i < 4; i++)
        {
            var u = source[i].U;
            var v = source[i].V;
            var x = destination[i].U;
            var z = destination[i].V;
            var row = 2 * i;
            system[row, 0] = u;
            system[row, 1] = v;
            system[row, 2] = 1;
            system[row, 6] = -x * u;
            system[row, 7] = -x * v;
            system[row, 8] = -x;
            system[row + 1, 3] = u;
            system[row + 1, 4] = v;
            system[row + 1, 5] = 1;
            system[row + 1, 6] = -z * u;
            system[row + 1, 7] = -z * v;
            system[row + 1, 8] = -z;
        }
        foreach (var value in system) maximum = Math.Max(maximum, Math.Abs(value));

        for (var pivot = 0; pivot < 8; pivot++)
        {
            var pivotRow = pivot;
            var pivotColumn = pivot;
            var pivotMagnitude = 0.0;
            for (var row = pivot; row < 8; row++)
            for (var col = pivot; col < 9; col++)
            {
                var magnitude = Math.Abs(system[row, col]);
                if (magnitude <= pivotMagnitude) continue;
                pivotMagnitude = magnitude;
                pivotRow = row;
                pivotColumn = col;
            }
            if (pivotMagnitude <= NumericalTolerance * maximum)
                throw new ArgumentException("Calibration does not determine a stable unique homography.");

            for (var col = 0; col < 9; col++)
                (system[pivot, col], system[pivotRow, col]) = (system[pivotRow, col], system[pivot, col]);
            for (var row = 0; row < 8; row++)
                (system[row, pivot], system[row, pivotColumn]) = (system[row, pivotColumn], system[row, pivot]);
            (permutation[pivot], permutation[pivotColumn]) = (permutation[pivotColumn], permutation[pivot]);

            var divisor = system[pivot, pivot];
            for (var col = pivot; col < 9; col++) system[pivot, col] /= divisor;
            for (var row = 0; row < 8; row++)
            {
                if (row == pivot) continue;
                var factor = system[row, pivot];
                for (var col = pivot; col < 9; col++) system[row, col] -= factor * system[pivot, col];
            }
        }

        var matrix = new double[9];
        matrix[permutation[8]] = 1;
        for (var row = 0; row < 8; row++) matrix[permutation[row]] = -system[row, 8];
        maximum = 0;
        foreach (var value in matrix) maximum = Math.Max(maximum, Math.Abs(value));
        for (var i = 0; i < 9; i++) matrix[i] /= maximum;
        return matrix;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private readonly struct Normalization
    {
        internal Normalization(double centerU, double centerV, double scale)
        {
            CenterU = centerU;
            CenterV = centerV;
            Scale = scale;
        }

        internal double CenterU { get; }
        internal double CenterV { get; }
        internal double Scale { get; }

        internal static Normalization Create(PixelPoint[] points, string parameterName)
        {
            var centerU = 0.0;
            var centerV = 0.0;
            foreach (var point in points)
            {
                centerU += point.U / 4;
                centerV += point.V / 4;
            }
            var meanDistance = 0.0;
            foreach (var point in points)
            {
                var du = point.U - centerU;
                var dv = point.V - centerV;
                meanDistance += Math.Sqrt(du * du + dv * dv) / 4;
            }
            var scale = Math.Sqrt(2) / meanDistance;
            if (!IsFinite(meanDistance) || meanDistance <= 0 || !IsFinite(scale) || scale <= 0)
                throw new ArgumentException("Calibration coordinates have insufficient spread or exceed the numeric range.", parameterName);
            return new Normalization(centerU, centerV, scale);
        }
    }
}
