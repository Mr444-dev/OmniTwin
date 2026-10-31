using System;
using System.IO;
using OmniTwin.Perception.Imaging;
using OpenCvSharp;
using Xunit;

namespace OmniTwin.Perception.Runtime.Tests;

public sealed class RasterContainerTests
{
    [Theory]
    [InlineData(".png", true)]
    [InlineData(".jpg", false)]
    public void AcceptsSingleBoundedRasterBeforeNativeDecode(string extension, bool expectedPng)
    {
        using var image = new Mat(31, 47, MatType.CV_8UC3, new Scalar(20, 40, 80));
        Assert.True(Cv2.ImEncode(extension, image, out var bytes));
        var result = RasterContainer.Inspect(bytes, 64, 4096);
        Assert.Equal(47, result.Width); Assert.Equal(31, result.Height); Assert.Equal(expectedPng, result.IsPng);
    }

    [Fact]
    public void RejectsTruncationCorruptedPngCrcOversizeDimensionsAndWrongFormat()
    {
        using var image = new Mat(31, 47, MatType.CV_8UC3, new Scalar(20, 40, 80));
        Assert.True(Cv2.ImEncode(".png", image, out var png));
        Assert.Throws<InvalidDataException>(() => RasterContainer.Inspect(png.AsSpan(0, png.Length - 3), 64, 4096));
        var corrupted = (byte[])png.Clone(); corrupted[29] ^= 0x40;
        Assert.Throws<InvalidDataException>(() => RasterContainer.Inspect(corrupted, 64, 4096));
        Assert.Throws<InvalidDataException>(() => RasterContainer.Inspect(png, 32, 4096));
        Assert.Throws<InvalidDataException>(() => RasterContainer.Inspect(new byte[] { 1, 2, 3, 4 }, 64, 4096));
        Assert.Throws<InvalidDataException>(() => RasterContainer.Inspect(png, 64, 16));
    }
}
