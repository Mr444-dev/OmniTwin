using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;

namespace OmniTwin.Transport;

/// <summary>A media operation error retaining a bounded safe category only.</summary>
public sealed class MediaTransportException : Exception
{
    /// <summary>Creates a non-sensitive failure.</summary>
    public MediaTransportException(string category) : base(category) => Category = category;
    /// <summary>Gets safe status text.</summary>
    public string Category { get; }
}
/// <summary>Asynchronous bounded local media transport. No provider credential is accepted or sent.</summary>
public sealed class MediaClient : IDisposable
{
    private readonly HttpClient _http;
    /// <summary>Creates a nonredirecting loopback client.</summary>
    public MediaClient() => _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    internal static Uri Endpoint(Uri origin, string path)
    {
        if (origin is null || origin.Scheme != "http" || !(origin.Host == "127.0.0.1" || origin.Host == "localhost") || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("Use a loopback HTTP origin.", nameof(origin));
        return new Uri(origin, path);
    }
    /// <summary>Uploads one owned short WAV; retries reuse the same idempotency identity.</summary>
    public async Task<VoiceTranscription> TranscribeAsync(Uri origin, Guid requestId, byte[] wav, CancellationToken token)
    {
        if (requestId == Guid.Empty || wav is null || wav.Length < 44 || wav.Length > MediaProtocolLimits.AudioMaximumBytes) throw new ArgumentException("Invalid command upload.");
        var endpoint = Endpoint(origin, "api/voice/transcribe"); var owned = (byte[])wav.Clone();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token); total.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(total.Token); budget.CancelAfter(TimeSpan.FromSeconds(35));
                try
                {
                    using var request = Request(HttpMethod.Post, endpoint); request.Headers.Add("X-OmniTwin-Voice-Request", requestId.ToString("D"));
                    request.Content = new ByteArrayContent(owned); request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var reason = await Failure(response, budget.Token).ConfigureAwait(false);
                        if (!Transient(response) || attempt >= 3) throw new MediaTransportException(reason);
                        await Delay(response, attempt, total.Token).ConfigureAwait(false); continue;
                    }
                    if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new MediaTransportException("invalid_voice_reply");
                    var result = MediaWireCodec.DecodeTranscription(await Bytes(response, 32768, budget.Token).ConfigureAwait(false));
                    if (result.RequestId != requestId) throw new MediaTransportException("voice_correlation_failed"); return result;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { if (total.IsCancellationRequested || attempt >= 3) throw new MediaTransportException("voice_timeout"); }
                catch (Exception error) when (error is HttpRequestException || error is IOException)
                { if (attempt >= 3) throw new MediaTransportException("voice_network_error"); }
                catch (WorldStreamProtocolException) { throw new MediaTransportException("invalid_voice_reply"); }
                await Task.Delay(250 * (1 << (attempt - 1)), total.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new MediaTransportException("voice_timeout"); }
        finally { Array.Clear(owned, 0, owned.Length); }
    }
    /// <summary>Fetches one fresh bounded JPEG with strict source headers; never waits for camera acquisition.</summary>
    public async Task<CameraPreview> ReadPreviewAsync(Uri origin, CancellationToken token)
    {
        var endpoint = Endpoint(origin, "api/camera/preview");
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token); total.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(total.Token); budget.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    using var request = Request(HttpMethod.Get, endpoint);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var reason = await Failure(response, budget.Token).ConfigureAwait(false);
                        if ((int)response.StatusCode == 503 || !Transient(response) || attempt >= 3) throw new MediaTransportException(reason);
                        await Delay(response, attempt, total.Token).ConfigureAwait(false); continue;
                    }
                    if (response.Content.Headers.ContentType?.MediaType != "image/jpeg") throw new MediaTransportException("invalid_camera_preview");
                    string Header(string name) { if (!response.Headers.TryGetValues(name, out var values)) throw new MediaTransportException("invalid_camera_headers"); var list = values.ToArray(); if (list.Length != 1) throw new MediaTransportException("invalid_camera_headers"); return list[0]; }
                    long Number(string name) { if (!long.TryParse(Header(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)) throw new MediaTransportException("invalid_camera_headers"); return value; }
                    if (!Guid.TryParseExact(Header("X-OmniTwin-Camera-Session"), "D", out var session) || !Guid.TryParseExact(Header("X-OmniTwin-Calibration"), "D", out var calibration)) throw new MediaTransportException("invalid_camera_headers");
                    var captured = Number("X-OmniTwin-Captured"); var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - captured;
                    if (age < -500 || age >= MediaProtocolLimits.PreviewFreshnessMilliseconds) throw new MediaTransportException("camera_preview_stale");
                    return new CameraPreview(session, calibration, Number("X-OmniTwin-Frame"), captured, checked((int)Number("X-OmniTwin-Width")), checked((int)Number("X-OmniTwin-Height")),
                        await Bytes(response, MediaProtocolLimits.PreviewMaximumBytes, budget.Token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { if (total.IsCancellationRequested || attempt >= 3) throw new MediaTransportException("camera_preview_timeout"); }
                catch (Exception error) when (error is HttpRequestException || error is IOException)
                { if (attempt >= 3) throw new MediaTransportException("camera_preview_network_error"); }
                catch (Exception error) when (error is WorldStreamProtocolException || error is OverflowException) { throw new MediaTransportException("invalid_camera_preview"); }
                await Task.Delay(100 * (1 << (attempt - 1)), total.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new MediaTransportException("camera_preview_timeout"); }
    }
    internal static HttpRequestMessage Request(HttpMethod method, Uri endpoint) { var request = new HttpRequestMessage(method, endpoint); request.Headers.Add("X-OmniTwin-Media", "1"); return request; }
    internal static bool Transient(HttpResponseMessage response) => (int)response.StatusCode is 408 or 429 or 500 or 502 or 504;
    internal static async Task Delay(HttpResponseMessage response, int attempt, CancellationToken token)
    {
        var duration = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(250 * (1 << (attempt - 1))));
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration > TimeSpan.FromSeconds(3)) throw new MediaTransportException("media_retry_exceeds_budget"); await Task.Delay(duration, token).ConfigureAwait(false);
    }
    internal static async Task<byte[]> Bytes(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new MediaTransportException("media_body_too_large");
        using var body = new MemoryStream(); using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false); var buffer = new byte[4096];
        while (true) { var count = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false); if (count == 0) break; if (body.Length + count > maximum) throw new MediaTransportException("media_body_too_large"); await body.WriteAsync(buffer, 0, count, token).ConfigureAwait(false); } return body.ToArray();
    }
    internal static async Task<string> Failure(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            using var json = JsonDocument.Parse(await Bytes(response, 1024, token).ConfigureAwait(false)); WorldStreamCodec.Keys(json.RootElement, "error"); var category = WorldStreamCodec.Text(json.RootElement, "error");
            if (category.Length <= 64 && category.All(character => (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '_')) return category;
        }
        catch (Exception error) when (error is JsonException || error is WorldStreamProtocolException || error is MediaTransportException) { return "invalid_media_error"; }
        return "invalid_media_error";
    }
    /// <summary>Releases transport after its owners cancel and await I/O.</summary>
    public void Dispose() => _http.Dispose();
}
