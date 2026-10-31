using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Perception.Imaging;
using OpenCvSharp;

namespace OmniTwin.Perception.Models;

/// <summary>Generates a bounded canonical tactical surface texture.</summary>
public interface IMuseImageClient
{
    /// <summary>Returns a validated, re-encoded and contrast-limited JPEG, never an arbitrary provider URL.</summary>
    Task<byte[]> GenerateAsync(string prompt, CancellationToken token);
}

/// <summary>Real Muse Image adapter with bounded inline images, native normalization, deadlines and safe retries.</summary>
public sealed class MuseImageClient : IMuseImageClient, IDisposable
{
    /// <summary>Official image-generation model checked on 2026-10-31.</summary>
    public const string ModelId = "muse-image-1.0";
    private readonly MetaModelTransport _transport;
    /// <summary>Loads META_API_KEY unless an owned protocol fixture injects a transport.</summary>
    public MuseImageClient(HttpClient? http = null, Uri? endpoint = null, string? apiKey = null)
        => _transport = new MetaModelTransport("images/generations", http, endpoint, apiKey);
    /// <inheritdoc />
    public async Task<byte[]> GenerateAsync(string prompt, CancellationToken token)
    {
        ValidatePrompt(prompt);
        var payload = new
        {
            model = ModelId, prompt = "Top-down seamless tabletop environment surface. No labels, objects, lines or tactical grid; those are rendered separately. Subtle texture only. " + prompt,
            n = 1, size = "1024x1024", response_format = "b64_json", output_format = "png", reasoning_strength = "low",
            tool_enablement = new { enable_image_search = false, enable_web_search = false, enable_shell = false }
        };
        var bytes = await _transport.SendAsync(payload, 12 * 1024 * 1024, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(120), token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 }); var root = json.RootElement;
            ModelJson.Check(root, new[] { "created", "data" }, new[] { "output_format", "background", "usage" });
            if (!root.GetProperty("created").TryGetInt64(out var created) || created < 0) throw new JsonException();
            var data = root.GetProperty("data"); if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 1) throw new JsonException();
            ModelJson.Check(data[0], new[] { "b64_json" }, new[] { "revised_prompt", "url" });
            if (data[0].TryGetProperty("url", out var url) && url.ValueKind != JsonValueKind.Null) throw new JsonException();
            var encoded = data[0].GetProperty("b64_json").GetString(); if (encoded is null || encoded.Length > 11_184_812) throw new JsonException();
            var image = Convert.FromBase64String(encoded); var info = RasterContainer.Inspect(image);
            if (root.TryGetProperty("output_format", out var format) && format.GetString() != (info.IsPng ? "png" : "jpeg")) throw new JsonException();
            token.ThrowIfCancellationRequested();
            using var decoded = Cv2.ImDecode(image, ImreadModes.Color);
            if (decoded.Empty() || decoded.Width != info.Width || decoded.Height != info.Height) throw new InvalidDataException();
            var scale = Math.Min(1, 1024.0 / Math.Max(info.Width, info.Height)); using var small = new Mat();
            Cv2.Resize(decoded, small, new Size(Math.Max(1, (int)Math.Round(info.Width * scale)), Math.Max(1, (int)Math.Round(info.Height * scale))), interpolation: InterpolationFlags.Area);
            using var tactical = new Mat(); small.ConvertTo(tactical, MatType.CV_8UC3, 0.16, 8);
            Cv2.ImEncode(".jpg", tactical, out var result, new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
            if (result.Length > 524288) throw new InvalidDataException();
            RasterContainer.Inspect(result, 1024, 524288); token.ThrowIfCancellationRequested(); return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException or InvalidDataException or OpenCVException)
        { throw new ModelApiException("invalid_generated_image"); }
    }
    /// <summary>Checks the same text bound before queueing or generation.</summary>
    public static void ValidatePrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 1000) throw new ArgumentException("Use a scenery description of 1–1000 characters.", nameof(prompt));
        foreach (var character in prompt) if (char.IsControl(character)) throw new ArgumentException("Scenery prompt contains controls.", nameof(prompt));
    }
    /// <summary>Releases the owned model transport after active requests stop.</summary>
    public void Dispose() => _transport.Dispose();
}
