using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Perception.Vision;
using OmniTwin.Perception.Tracking;
using OmniTwin.Transport;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || (args.Length == 1 && args[0] == "--help"))
        {
            Console.WriteLine("OmniTwin perception\n  capture <device> <seconds>\n  snapshot <device> <output.jpg>\n  segment <image.jpg> <noun phrase>\n  watch <device> <seconds> <noun phrase>\n  calibration-check <calibration.json>\n  watch-world <device> <seconds> <noun phrase> <calibration.json> [--hazard]\n  stream-probe <ws://host/ws/twin> <message count>\nUse META_API_KEY for inference. Quote the noun phrase as one shell argument.");
            return 0;
        }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (args[0] == "snapshot" && args.Length == 3)
            {
                if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var device))
                    throw new ArgumentException("Device must be a nonnegative integer.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                try { Write(await CameraSnapshotWriter.CaptureAsync(device, args[2], deadline.Token).ConfigureAwait(false)); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                { Write(new { Category = "capture_timeout" }); return 2; }
                return 0;
            }
            if (args[0] == "stream-probe" && args.Length == 3)
            {
                if (!Uri.TryCreate(args[1], UriKind.Absolute, out var endpoint) ||
                    !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                    throw new ArgumentException("Provide a WebSocket URL and a message count from one through 300.");
                await StreamProbe.RunAsync(endpoint, count, Write, stop.Token).ConfigureAwait(false);
                return 0;
            }
            if (args[0] == "calibration-check" && args.Length == 2)
            {
                var profile = await CalibrationProfile.LoadAsync(args[1], stop.Token).ConfigureAwait(false);
                Write(new { profile.CalibrationId, profile.ImageWidth, profile.ImageHeight, profile.MinimumRadiusMeters,
                    profile.MaximumRadiusMeters, profile.SafetyMarginMeters, profile.MinimumConfidence });
                return 0;
            }
            if (args[0] == "segment" && args.Length == 3)
            {
                using var client = Sam31Client.FromEnvironment();
                var info = new FileInfo(args[1]);
                if (!info.Exists || info.Length > 8_388_608) throw new ArgumentException("Provide an existing JPEG of at most 8 MiB.");
                var bytes = await File.ReadAllBytesAsync(info.FullName, stop.Token).ConfigureAwait(false);
                var frame = await Task.Run(() => CapturedFrame.FromJpeg(bytes), stop.Token).ConfigureAwait(false);
                Write(await client.SegmentAsync(frame, args[2], stop.Token).ConfigureAwait(false));
                return 0;
            }
            if ((args[0] == "capture" && args.Length == 3) || (args[0] == "watch" && args.Length == 4) ||
                (args[0] == "watch-world" && (args.Length == 5 || (args.Length == 6 && args[5] == "--hazard"))))
            {
                if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var device) ||
                    !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 1 || seconds > 300)
                    throw new ArgumentException("Device must be nonnegative; duration must be 1 through 300 seconds.");
                var profile = args[0] == "watch-world" ? await CalibrationProfile.LoadAsync(args[4], stop.Token).ConfigureAwait(false) : null;
                stop.CancelAfter(TimeSpan.FromSeconds(seconds));
                var camera = new CameraCaptureWorker(device, profile?.ImageWidth ?? 640, profile?.ImageHeight ?? 480);
                if (args[0] == "capture")
                {
                    await foreach (var frame in camera.ReadFramesAsync(stop.Token).ConfigureAwait(false))
                        Write(new { frame.SequenceNumber, frame.CapturedAt, frame.Width, frame.Height, frame.Change });
                }
                else
                {
                    using var client = Sam31Client.FromEnvironment();
                    var worker = new SegmentationWorker(client);
                    var frames = camera.ReadFramesAsync(stop.Token);
                    if (profile is not null) frames = CalibratedFrameSource.ValidateAsync(frames, profile, stop.Token);
                    var segmentation = worker.RunAsync(frames, args[3], stop.Token);
                    if (profile is null)
                    {
                        await foreach (var update in segmentation.ConfigureAwait(false)) Write(update);
                    }
                    else
                    {
                        var tracking = new CalibratedTrackingWorker(profile, args[3], args.Length == 6);
                        await foreach (var update in tracking.RunHybridAsync(frames, client, stop.Token).ConfigureAwait(false)) Write(update);
                    }
                }
                return 0;
            }
            throw new ArgumentException("Invalid command. Run with --help.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (SamApiException error)
        { Write(new { error.Category, error.Attempts, error.StatusCode }); return 2; }
        catch (WorldStreamProtocolException error) { Write(new { error.Category }); return 2; }
        catch (WorldStreamConnectionException error) { Write(new { error.Category }); return 2; }
        catch (Exception error)
        {
            Console.Error.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value));

}
