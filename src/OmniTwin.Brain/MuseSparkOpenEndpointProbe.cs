using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace OmniTwin.Brain;

/// <summary>A sanitized Baseten endpoint result; the provider response body is never retained.</summary>
public sealed record MuseSparkOpenProbeResult(int HttpStatusCode, string ModelIdUsed, bool UsedModelFallback, double LatencyMilliseconds);

/// <summary>A safe endpoint failure with a stable category and optional HTTP status only.</summary>
public sealed class MuseSparkOpenProbeException : Exception
{
    /// <summary>Creates a sanitized endpoint failure.</summary>
    public MuseSparkOpenProbeException(string category, string modelId, int? httpStatusCode = null) : base(category)
    { Category = category; ModelIdUsed = modelId; HttpStatusCode = httpStatusCode; }
    /// <summary>Gets a bounded failure category.</summary>
    public string Category { get; }
    /// <summary>Gets the exact model ID used for the last attempt.</summary>
    public string ModelIdUsed { get; }
    /// <summary>Gets the upstream HTTP code, if a response was received.</summary>
    public int? HttpStatusCode { get; }
}

/// <summary>Executes a minimal authenticated completion against the required Baseten model.</summary>
public static class MuseSparkOpenEndpointProbe
{
    /// <summary>Runs a real short request; only a dated model's 404 triggers one retry with the official alias.</summary>
    public static async Task<MuseSparkOpenProbeResult> RunAsync(CancellationToken token = default)
    {
        var model = MuseSparkOpenClient.RequiredModel;
        var key = Environment.GetEnvironmentVariable("BASETEN_API_KEY");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new MuseSparkOpenProbeException("auth_missing_or_invalid_401", model);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var clock = Stopwatch.StartNew(); var failures = 0;
        while (true)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); attempt.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { model, stream = false, max_tokens = 8,
                    messages = new[] { new { role = "user", content = "Reply with the single word OK." } } });
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://inference.baseten.co/v1/chat/completions")
                { Content = new ByteArrayContent(payload) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound && model == MuseSparkOpenClient.RequiredModel)
                { model = MuseSparkOpenClient.AliasModel; continue; }
                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    var category = code switch { 401 => "auth_missing_or_invalid_401", 403 => "permission_denied_403", 429 => "rate_limited_429", 408 or 504 => "upstream_timeout", 404 => "model_not_found_404", _ => "upstream_error" };
                    var retry = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || code >= 500;
                    if (!retry || ++failures >= 3) throw new MuseSparkOpenProbeException(category, model, code);
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(250 * (1 << (failures - 1)));
                    if (delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(2)) delay = TimeSpan.FromSeconds(2);
                    await Task.Delay(delay, deadline.Token).ConfigureAwait(false); continue;
                }
                if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength > 65536)
                    throw new MuseSparkOpenProbeException("invalid_response_schema", model, (int)response.StatusCode);
                await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
                using var body = new MemoryStream(); var buffer = new byte[4096];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, attempt.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (body.Length + count > 65536) throw new MuseSparkOpenProbeException("response_size_limit", model, (int)response.StatusCode);
                    await body.WriteAsync(buffer.AsMemory(0, count), attempt.Token).ConfigureAwait(false);
                }
                try
                {
                    using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1 ||
                        choices[0].ValueKind != JsonValueKind.Object || !choices[0].TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                        !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString()))
                        throw new JsonException();
                }
                catch (JsonException) { throw new MuseSparkOpenProbeException("invalid_response_schema", model, (int)response.StatusCode); }
                return new MuseSparkOpenProbeResult((int)response.StatusCode, model, model == MuseSparkOpenClient.AliasModel, clock.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                if (deadline.IsCancellationRequested || ++failures >= 3) throw new MuseSparkOpenProbeException("upstream_timeout", model);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << (failures - 1))), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                if (++failures >= 3) throw new MuseSparkOpenProbeException("network_error", model);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << (failures - 1))), deadline.Token).ConfigureAwait(false);
            }
        }
    }
}
