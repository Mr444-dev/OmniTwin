using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Brain;
using OmniTwin.Perception.Audio;
using OmniTwin.Perception.Models;
using OmniTwin.Perception.Vision;
using OpenCvSharp;

internal static class LivePreflight
{
    private sealed record ProbeRow(string Model, string ModelId, string Status, int? HttpStatus, double? Milliseconds, string Detail);

    public static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        var known = new HashSet<string>(new[] { "--probe-models", "--synthetic-tabletop-replay" }, StringComparer.Ordinal);
        if (args.Any(argument => !known.Contains(argument)) || args.Distinct(StringComparer.Ordinal).Count() != args.Length)
        {
            Console.Error.WriteLine("Usage: live-preflight [--probe-models] [--synthetic-tabletop-replay]");
            return 2;
        }
        var probeModels = Array.Exists(args, value => value == "--probe-models");
        var synthetic = Array.Exists(args, value => value == "--synthetic-tabletop-replay");
        var metaConfigured = Present("META_API_KEY");
        var basetenConfigured = Present("BASETEN_API_KEY");
        Console.WriteLine("OMNITWIN LIVE PREFLIGHT");
        Console.WriteLine("SOURCE: " + (synthetic ? "SYNTHETIC TABLETOP REPLAY (NON-LIVE)" : "LIVE CAMERA REQUESTED"));
        Console.WriteLine("META_API_KEY: " + (metaConfigured ? "configured" : "missing"));
        Console.WriteLine("BASETEN_API_KEY: " + (basetenConfigured ? "configured" : "missing"));
        Console.WriteLine("Credential values are never printed. Use --probe-models for real inference requests (may incur provider charges).");
        Console.WriteLine();
        Console.WriteLine("{0,-24} {1,-42} {2,-30} {3,5} {4,11}  {5}", "MODEL", "MODEL ID", "STATUS", "HTTP", "LATENCY ms", "DETAIL");
        Console.WriteLine(new string('-', 144));
        var rows = new List<ProbeRow>(7);
        var cameraDetail = synthetic ? "No camera opened; deterministic local CV" : "";
        var camera = synthetic ? "skipped" : ProbeCamera(out cameraDetail);
        Console.WriteLine("{0,-24} {1,-42} {2,-30} {3,5} {4,11}  {5}", "Camera", synthetic ? "synthetic replay" : "device 0..3", camera, "-", "-", synthetic ? "No camera opened; deterministic local CV" : cameraDetail);
        AddCredentialRow(rows, "SAM 3.1", "sam-3.1", metaConfigured, probeModels);
        AddCredentialRow(rows, "Muse Spark 1.3", MuseSparkSemanticClient.ModelId, metaConfigured, probeModels);
        AddCredentialRow(rows, "Muse Voice", MuseVoiceClient.ModelId, metaConfigured, probeModels);
        AddCredentialRow(rows, "Muse Image", MuseImageClient.ModelId, metaConfigured, probeModels);
        AddCredentialRow(rows, "Muse Spark Open", MuseSparkOpenClient.RequiredModel, basetenConfigured, probeModels);
        if (probeModels)
        {
            byte[] jpeg;
            try { jpeg = MakeProbeJpeg(); }
            catch (Exception error) when (error is OpenCVException || error is InvalidOperationException)
            { Console.Error.WriteLine("Unable to construct local probe image: " + error.GetType().Name); return 2; }
            var frame = CapturedFrame.FromJpeg(jpeg);
            if (metaConfigured)
            {
                await AddAsync(rows, "SAM 3.1", "sam-3.1", async () =>
                {
                    using var client = Sam31Client.FromEnvironment(new SamClientOptions { MaximumAttempts = 1, AttemptTimeout = TimeSpan.FromSeconds(6), TotalTimeout = TimeSpan.FromSeconds(8) });
                    var result = await client.SegmentAsync(frame, "tabletop object", token).ConfigureAwait(false);
                    return new ProbeRow("SAM 3.1", "sam-3.1", "success", (int)HttpStatusCode.OK, result.DurationMs, "minimal image segmentation");
                }).ConfigureAwait(false);
                await AddAsync(rows, "Muse Spark 1.3", MuseSparkSemanticClient.ModelId, async () =>
                {
                    using var client = new MuseSparkSemanticClient(); var watch = Stopwatch.StartNew();
                    _ = await client.ClassifyAsync(jpeg, token).ConfigureAwait(false);
                    return new ProbeRow("Muse Spark 1.3", MuseSparkSemanticClient.ModelId, "success", 200, watch.Elapsed.TotalMilliseconds, "minimal image classification");
                }).ConfigureAwait(false);
                await AddAsync(rows, "Muse Voice", MuseVoiceClient.ModelId, async () =>
                {
                    using var client = new MuseVoiceClient(); var audio = PcmWaveCodec.Encode(new float[3200], PcmWaveCodec.SampleRate, 1); var watch = Stopwatch.StartNew();
                    _ = await client.TranscribeAsync(Guid.NewGuid(), audio, token).ConfigureAwait(false);
                    return new ProbeRow("Muse Voice", MuseVoiceClient.ModelId, "success", 200, watch.Elapsed.TotalMilliseconds, "0.2 s silence transcription probe");
                }).ConfigureAwait(false);
                await AddAsync(rows, "Muse Image", MuseImageClient.ModelId, async () =>
                {
                    using var client = new MuseImageClient(); var watch = Stopwatch.StartNew();
                    _ = await client.GenerateAsync("Subtle neutral industrial tabletop surface, no labels or objects.", token).ConfigureAwait(false);
                    return new ProbeRow("Muse Image", MuseImageClient.ModelId, "success", 200, watch.Elapsed.TotalMilliseconds, "generated one validation image; may incur charges");
                }).ConfigureAwait(false);
            }
            if (basetenConfigured)
                await AddAsync(rows, "Muse Spark Open", MuseSparkOpenClient.RequiredModel, async () =>
                {
                    var result = await MuseSparkOpenEndpointProbe.RunAsync(token).ConfigureAwait(false);
                    return new ProbeRow("Muse Spark Open", result.ModelIdUsed, "success", result.HttpStatusCode, result.LatencyMilliseconds,
                        result.UsedModelFallback ? "official alias used after dated-ID HTTP 404" : "required dated model accepted");
                }).ConfigureAwait(false);
        }
        foreach (var row in rows)
            Console.WriteLine("{0,-24} {1,-42} {2,-30} {3,5} {4,11}  {5}", row.Model, row.ModelId, row.Status,
                row.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-", row.Milliseconds?.ToString("F0", CultureInfo.InvariantCulture) ?? "-", row.Detail);
        if (!probeModels)
            Console.WriteLine("Model endpoints: not probed. Pass --probe-models to send one minimal request to each configured provider model.");
        var failed = camera == "unavailable" || rows.Any(row => row.Status is "auth_missing_or_invalid_401" or "permission_denied_403" or "rate_limited_429" or "upstream_timeout" or "network_error" or "model_not_found_404" or "upstream_error" or "invalid_response_schema" or "local_probe_error" or "cancelled");
        if (probeModels && (!metaConfigured || !basetenConfigured)) failed = true;
        return failed ? 2 : 0;
    }

    private static async Task AddAsync(List<ProbeRow> rows, string model, string id, Func<Task<ProbeRow>> run)
    {
        try { rows.Add(await run().ConfigureAwait(false)); }
        catch (SamApiException error) { rows.Add(new ProbeRow(model, id, error.OperatorCategory, error.StatusCode.HasValue ? (int)error.StatusCode.Value : null, null, "sanitized provider failure")); }
        catch (ModelApiException error) { rows.Add(new ProbeRow(model, id, error.Category, error.HttpStatusCode, null, "sanitized provider failure")); }
        catch (VoiceApiException error) { rows.Add(new ProbeRow(model, id, error.Category, error.HttpStatusCode, null, "sanitized provider failure")); }
        catch (MuseSparkOpenProbeException error) { rows.Add(new ProbeRow(model, error.ModelIdUsed, error.Category, error.HttpStatusCode, null, "sanitized provider failure")); }
        catch (OperationCanceledException) { rows.Add(new ProbeRow(model, id, "cancelled", null, null, "operator cancelled")); }
        catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is OpenCVException)
        { rows.Add(new ProbeRow(model, id, "local_probe_error", null, null, error.GetType().Name)); }
    }

    private static void AddCredentialRow(List<ProbeRow> rows, string model, string modelId, bool key, bool requested)
    {
        if (!key) rows.Add(new ProbeRow(model, modelId, "skipped", null, null, "provider credential missing"));
        else if (!requested) rows.Add(new ProbeRow(model, modelId, "not_probed", null, null, "use --probe-models for live HTTP validation"));
    }

    private static bool Present(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    private static string ProbeCamera(out string detail)
    {
        for (var index = 0; index <= 3; index++)
        {
            try
            {
                using var capture = new VideoCapture(index);
                if (!capture.IsOpened()) continue;
                using var frame = new Mat();
                if (!capture.Read(frame) || frame.Empty()) continue;
                detail = string.Format(CultureInfo.InvariantCulture, "device {0} · {1}×{2}", index, frame.Width, frame.Height);
                return "available";
            }
            catch (OpenCVException) { }
        }
        detail = "no readable camera among indices 0..3";
        return "unavailable";
    }

    private static byte[] MakeProbeJpeg()
    {
        using var image = new Mat(64, 64, MatType.CV_8UC3, new Scalar(24, 30, 35));
        Cv2.Rectangle(image, new Rect(16, 18, 32, 28), new Scalar(160, 120, 60), -1);
        Cv2.ImEncode(".jpg", image, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 75));
        if (jpeg.Length == 0 || jpeg.Length > 65536) throw new InvalidOperationException("probe_image_encode_failed");
        return jpeg;
    }
}
