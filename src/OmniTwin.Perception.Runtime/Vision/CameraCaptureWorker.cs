using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using OpenCvSharp;

namespace OmniTwin.Perception.Vision;

/// <summary>Reads a local camera on an owned background task and publishes only the latest owned frame.</summary>
/// <remarks>Native reads cannot be forcibly cancelled; shutdown waits for the current driver call to return.</remarks>
public sealed class CameraCaptureWorker
{
    private readonly int _device;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;

    /// <summary>Configures a local camera. Requested resolution and rate remain subject to driver support.</summary>
    public CameraCaptureWorker(int device = 0, int width = 640, int height = 480, int framesPerSecond = 30)
    {
        if (device < 0) throw new ArgumentOutOfRangeException(nameof(device));
        if (width <= 0 || height <= 0 || (long)width * height > 4_194_304) throw new ArgumentOutOfRangeException(nameof(width));
        if (framesPerSecond < 1 || framesPerSecond > 120) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        _device = device; _width = width; _height = height; _fps = framesPerSecond;
    }

    /// <summary>Starts an independent camera session; unread frames are replaced rather than accumulated.</summary>
    public async IAsyncEnumerable<CapturedFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(1)
        {
            SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        var producer = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                using var capture = new VideoCapture(_device, VideoCaptureAPIs.ANY);
                if (!capture.IsOpened()) throw new IOException($"Cannot open camera device {_device}.");
                capture.Set(VideoCaptureProperties.FrameWidth, _width);
                capture.Set(VideoCaptureProperties.FrameHeight, _height);
                capture.Set(VideoCaptureProperties.Fps, _fps);
                using var image = new Mat();
                using var gray = new Mat();
                var trigger = new FrameChangeTrigger();
                var clock = Stopwatch.StartNew();
                var sequence = 0L;
                var actualWidth = 0;
                var actualHeight = 0;
                while (!stop.IsCancellationRequested)
                {
                    var started = clock.Elapsed;
                    if (!capture.Read(image) || image.Empty()) throw new IOException("Camera stopped supplying frames.");
                    var capturedAt = DateTimeOffset.UtcNow;
                    var captureTimestamp = Stopwatch.GetTimestamp();
                    if ((long)image.Width * image.Height > 4_194_304 || image.Type() != MatType.CV_8UC3)
                        throw new InvalidDataException("Camera must supply BGR frames of at most four megapixels.");
                    if (sequence > 0 && (image.Width != actualWidth || image.Height != actualHeight))
                        throw new InvalidDataException("Camera resolution changed; start a new calibrated session.");
                    actualWidth = image.Width;
                    actualHeight = image.Height;
                    Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
                    var pixelCount = checked(gray.Width * gray.Height);
                    if (!gray.IsContinuous()) throw new InvalidDataException("Grayscale buffer is unexpectedly strided.");
                    var pixels = ArrayPool<byte>.Shared.Rent(pixelCount);
                    FrameChangeDecision decision;
                    try
                    {
                        Marshal.Copy(gray.Data, pixels, 0, pixelCount);
                        decision = trigger.Observe(pixels.AsSpan(0, pixelCount), clock.ElapsedMilliseconds);
                    }
                    finally { ArrayPool<byte>.Shared.Return(pixels); }
                    Cv2.ImEncode(".jpg", image, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 85));
                    if (jpeg.Length == 0 || jpeg.Length > 8_388_608)
                        throw new InvalidDataException("JPEG encoding failed.");
                    channel.Writer.TryWrite(new CapturedFrame(sequence++, capturedAt, image.Width, image.Height, jpeg, decision, captureTimestamp));
                    var remaining = TimeSpan.FromSeconds(1.0 / _fps) - (clock.Elapsed - started);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error) { failure = error; }
            finally { channel.Writer.TryComplete(failure); }
        }, CancellationToken.None);
        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return frame;
        }
        finally
        {
            stop.Cancel();
            await producer.ConfigureAwait(false);
        }
    }
}
