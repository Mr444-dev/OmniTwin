using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OmniTwin.Perception.Vision;

/// <summary>Describes a locally saved canonical camera JPEG and its actual capture dimensions.</summary>
public sealed record CameraSnapshotResult(string Path, int Width, int Height, DateTimeOffset CapturedAt);

/// <summary>Saves one owned camera frame without overwriting an existing file.</summary>
public static class CameraSnapshotWriter
{
    /// <summary>Captures on the camera worker, then writes a JPEG asynchronously to an explicit local path.</summary>
    /// <remarks>Uses the perception defaults (640 × 480 requested). Native driver calls can delay cancellation.</remarks>
    public static Task<CameraSnapshotResult> CaptureAsync(int device, string path, CancellationToken cancellationToken = default)
    {
        var camera = new CameraCaptureWorker(device);
        return WriteFirstAsync(camera.ReadFramesAsync(cancellationToken), path, cancellationToken);
    }

    internal static async Task<CameraSnapshotResult> WriteFirstAsync(IAsyncEnumerable<CapturedFrame> frames, string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var extension = System.IO.Path.GetExtension(fullPath);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Snapshot output must have a .jpg or .jpeg extension.", nameof(path));
        if (File.Exists(fullPath)) throw new IOException("Snapshot already exists; choose a new output path.");
        cancellationToken.ThrowIfCancellationRequested();
        CapturedFrame? captured = null;
        await foreach (var frame in frames.WithCancellation(cancellationToken).ConfigureAwait(false))
        { captured = frame; break; }
        if (captured is null) throw new IOException("Camera supplied no frame.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var ownsTemporary = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous))
            {
                ownsTemporary = true;
                await output.WriteAsync(captured.CopyJpeg(), cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: false);
        }
        finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
        return new CameraSnapshotResult(fullPath, captured.Width, captured.Height, captured.CapturedAt);
    }
}
