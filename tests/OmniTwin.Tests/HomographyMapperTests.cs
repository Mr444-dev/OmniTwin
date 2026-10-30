using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Perception.Calibration;
using Xunit;

namespace OmniTwin.Tests;

public sealed class HomographyMapperTests
{
    private static PixelPoint[] CameraCorners() =>
        new[] { new PixelPoint(0, 0), new PixelPoint(640, 0), new PixelPoint(640, 480), new PixelPoint(0, 480) };

    private static Vector2D[] WorldCorners() =>
        new[] { new Vector2D(-10, -6.25f), new Vector2D(10, -6.25f), new Vector2D(10, 6.25f), new Vector2D(-10, 6.25f) };

    [Theory]
    [InlineData(0, 0, -10, -6.25)]
    [InlineData(640, 0, 10, -6.25)]
    [InlineData(640, 480, 10, 6.25)]
    [InlineData(0, 480, -10, 6.25)]
    [InlineData(320, 240, 0, 0)]
    [InlineData(160, 120, -5, -3.125)]
    [InlineData(800, 240, 15, 0)]
    public void MapsRectangleAndDocumentedExtrapolation(double u, double v, float x, float z)
    {
        var mapper = new HomographyMapper(CameraCorners(), WorldCorners());
        AssertClose(new Vector2D(x, z), mapper.PixelToWorld(u, v));
    }

    [Fact]
    public void MatchesIndependentPerspectiveOracleAcrossInteriorGrid()
    {
        var corners = CameraCorners();
        var mapper = new HomographyMapper(corners, corners.Select(point => Perspective(point.U, point.V)).ToArray());
        for (var u = 0; u <= 640; u += 32)
        for (var v = 0; v <= 480; v += 24)
            AssertClose(Perspective(u, v), mapper.PixelToWorld(u, v), 2e-5);
    }

    [Fact]
    public void SupportsClockwiseOrderingAndReflectedWorldAxes()
    {
        var mapper = new HomographyMapper(CameraCorners().Reverse().ToArray(), WorldCorners());
        AssertClose(new Vector2D(-5, 3.125f), mapper.PixelToWorld(160, 120));
    }

    [Fact]
    public void NormalizesLargePixelOffsets()
    {
        const double offset = 1e12;
        var source = CameraCorners().Select(point => new PixelPoint(point.U + offset, point.V - offset)).ToArray();
        var mapper = new HomographyMapper(source, WorldCorners());
        AssertClose(new Vector2D(-5, -3.125f), mapper.PixelToWorld(offset + 160, -offset + 120));
    }

    [Fact]
    public void DefensivelyCopiesCalibrationInputs()
    {
        var source = CameraCorners();
        var destination = WorldCorners();
        var mapper = new HomographyMapper(source, destination);
        source[0] = new PixelPoint(100, 100);
        destination[0] = new Vector2D(500, 500);
        AssertClose(new Vector2D(-10, -6.25f), mapper.PixelToWorld(0, 0));
    }

    [Fact]
    public void SupportsConcurrentReads()
    {
        var mapper = new HomographyMapper(CameraCorners(), WorldCorners());
        Parallel.For(0, 10_000, index =>
        {
            var u = index % 641;
            var v = index % 481;
            AssertClose(new Vector2D((float)(u / 32.0 - 10), (float)(v / 38.4 - 6.25)), mapper.PixelToWorld(u, v));
        });
    }

    [Fact]
    public void RejectsProjectionAtAndNearHorizon()
    {
        var source = new[] { new PixelPoint(0, 0), new PixelPoint(2, 0), new PixelPoint(2, 1), new PixelPoint(0, 1) };
        var destination = new[] { new Vector2D(0, 0), new Vector2D(2, 0), new Vector2D(1, 0.5f), new Vector2D(0, 0.5f) };
        var mapper = new HomographyMapper(source, destination);
        Assert.False(mapper.TryPixelToWorld(1, -1, out _));
        Assert.False(mapper.TryPixelToWorld(1, -1 + 1e-14, out _));
        Assert.Throws<InvalidOperationException>(() => mapper.PixelToWorld(1, -1));
        AssertClose(new Vector2D(2f / 3f, 1f / 3f), mapper.PixelToWorld(1, 0.5));
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NegativeInfinity)]
    [InlineData(0, double.NaN)]
    public void RejectsNonfinitePixelQueries(double u, double v)
    {
        var mapper = new HomographyMapper(CameraCorners(), WorldCorners());
        Assert.False(mapper.TryPixelToWorld(u, v, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.PixelToWorld(u, v));
    }

    [Fact]
    public void RejectsWorldOverflow()
    {
        var world = new[] { new Vector2D(-2e38f, -2e38f), new Vector2D(2e38f, -2e38f),
            new Vector2D(2e38f, 2e38f), new Vector2D(-2e38f, 2e38f) };
        var mapper = new HomographyMapper(CameraCorners(), world);
        Assert.False(mapper.TryPixelToWorld(1280, 240, out _));
        Assert.Throws<InvalidOperationException>(() => mapper.PixelToWorld(1280, 240));
    }

    public static IEnumerable<object[]> InvalidPolygons()
    {
        yield return new object[] { new[] { new PixelPoint(0, 0), new PixelPoint(0, 0), new PixelPoint(1, 1), new PixelPoint(0, 1) } };
        yield return new object[] { new[] { new PixelPoint(0, 0), new PixelPoint(1, 0), new PixelPoint(2, 0), new PixelPoint(0, 1) } };
        yield return new object[] { new[] { new PixelPoint(0, 0), new PixelPoint(1, 1), new PixelPoint(1, 0), new PixelPoint(0, 1) } };
        yield return new object[] { new[] { new PixelPoint(0, 0), new PixelPoint(2, 0), new PixelPoint(0.5, 0.25), new PixelPoint(0, 1) } };
        yield return new object[] { new[] { new PixelPoint(0, 0), new PixelPoint(1, 0), new PixelPoint(1, 1e-12), new PixelPoint(0, 1e-12) } };
        yield return new object[] { new PixelPoint[4] };
    }

    [Theory]
    [MemberData(nameof(InvalidPolygons))]
    public void RejectsDegenerateOrUnorderedCameraCorners(PixelPoint[] corners) =>
        Assert.Throws<ArgumentException>(() => new HomographyMapper(corners, WorldCorners()));

    [Fact]
    public void RejectsDegenerateWorldCorners() =>
        Assert.Throws<ArgumentException>(() => new HomographyMapper(CameraCorners(), new Vector2D[4]));

    [Fact]
    public void RejectsIncorrectCornerCountsAndNullInputs()
    {
        Assert.Throws<ArgumentException>(() => new HomographyMapper(Array.Empty<PixelPoint>(), WorldCorners()));
        Assert.Throws<ArgumentException>(() => new HomographyMapper(CameraCorners(), Array.Empty<Vector2D>()));
        Assert.Throws<ArgumentNullException>(() => new HomographyMapper(null!, WorldCorners()));
        Assert.Throws<ArgumentNullException>(() => new HomographyMapper(CameraCorners(), null!));
    }

    [Fact]
    public void RejectsNonfinitePixelConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelPoint(double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelPoint(0, double.PositiveInfinity));
    }

    private static Vector2D Perspective(double u, double v)
    {
        var denominator = 0.001 * u + 0.0005 * v + 1;
        return new Vector2D((float)((0.03 * u + 0.01 * v - 4) / denominator),
            (float)((-0.005 * u + 0.025 * v - 3) / denominator));
    }

    private static void AssertClose(Vector2D expected, Vector2D actual, double tolerance = 1e-5)
    {
        Assert.InRange(Math.Abs((double)expected.X - actual.X), 0, tolerance);
        Assert.InRange(Math.Abs((double)expected.Z - actual.Z), 0, tolerance);
    }
}
