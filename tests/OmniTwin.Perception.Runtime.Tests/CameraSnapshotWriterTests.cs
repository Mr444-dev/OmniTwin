using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Perception.Vision;
using OpenCvSharp;
using Xunit;

namespace OmniTwin.Perception.Runtime.Tests;

public sealed class CameraSnapshotWriterTests
{
    [Fact]
    public async Task WritesAnOwnedJpegAndClosesTheFrameSourceBeforeReturning()
    {
        using var directory = new TemporaryDirectory();
        using var pixels = new Mat(24, 32, MatType.CV_8UC3, new Scalar(20, 100, 200));
        Cv2.ImEncode(".jpg", pixels, out var jpeg);
        var frame = CapturedFrame.FromJpeg(jpeg);
        var closed = false;
        var path = Path.Combine(directory.Path, "nested", "table.jpg");
        var result = await CameraSnapshotWriter.WriteFirstAsync(Frames(frame, () => closed = true, TestContext.Current.CancellationToken), path, TestContext.Current.CancellationToken);
        Assert.True(closed);
        Assert.Equal(path, result.Path);
        Assert.Equal(32, result.Width);
        Assert.Equal(24, result.Height);
        Assert.Equal(frame.CapturedAt, result.CapturedAt);
        var saved = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(frame.CopyJpeg(), saved);
        Assert.Equal(32, CapturedFrame.FromJpeg(saved).Width);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task ExistingFileIsPreservedWithoutOpeningTheSource()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "existing.jpg");
        await File.WriteAllTextAsync(path, "existing user file", TestContext.Current.CancellationToken);
        var opened = false;
        await Assert.ThrowsAsync<IOException>(() => CameraSnapshotWriter.WriteFirstAsync(Empty(() => opened = true), path, TestContext.Current.CancellationToken));
        Assert.False(opened);
        Assert.Equal("existing user file", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmptyOrFailedCaptureDoesNotCreateAnOutput()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "empty.jpg");
        await Assert.ThrowsAsync<IOException>(() => CameraSnapshotWriter.WriteFirstAsync(Empty(() => { }), path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CancellationAndInvalidExtensionAreRejectedBeforeAcquisition()
    {
        using var directory = new TemporaryDirectory();
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var opened = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CameraSnapshotWriter.WriteFirstAsync(Empty(() => opened = true),
            Path.Combine(directory.Path, "cancelled.jpg"), stop.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => CameraSnapshotWriter.WriteFirstAsync(Empty(() => opened = true),
            Path.Combine(directory.Path, "wrong.png"), TestContext.Current.CancellationToken));
        Assert.False(opened);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task CompetingWritersCannotOverwriteAndLeaveNoTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        using var pixels = new Mat(24, 32, MatType.CV_8UC3, new Scalar(40, 70, 120));
        Cv2.ImEncode(".jpg", pixels, out var jpeg);
        var frame = CapturedFrame.FromJpeg(jpeg);
        var path = Path.Combine(directory.Path, "race.jpg");
        var first = Attempt();
        var second = Attempt();
        var results = await Task.WhenAll(first, second);
        Assert.Single(Array.FindAll(results, success => success));
        Assert.Equal(frame.CopyJpeg(), await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(directory.Path));
        async Task<bool> Attempt()
        {
            try { await CameraSnapshotWriter.WriteFirstAsync(Frames(frame, () => { }, TestContext.Current.CancellationToken), path, TestContext.Current.CancellationToken); return true; }
            catch (IOException) { return false; }
        }
    }

    private static async IAsyncEnumerable<CapturedFrame> Frames(CapturedFrame frame, Action closed,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        try { await Task.Yield(); token.ThrowIfCancellationRequested(); yield return frame; }
        finally { closed(); }
    }

    private static async IAsyncEnumerable<CapturedFrame> Empty(Action opened)
    { opened(); await Task.Yield(); yield break; }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OmniTwin-snapshot-" + Guid.NewGuid().ToString("N"));
        internal TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            // This test owns only files immediately below this unique directory and its known nested directory.
            var nested = System.IO.Path.Combine(Path, "nested");
            if (Directory.Exists(nested)) { foreach (var file in Directory.GetFiles(nested)) File.Delete(file); Directory.Delete(nested); }
            foreach (var file in Directory.GetFiles(Path)) File.Delete(file);
            Directory.Delete(Path);
        }
    }
}
