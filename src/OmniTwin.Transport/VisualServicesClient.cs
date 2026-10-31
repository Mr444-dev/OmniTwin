using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Perception.Imaging;

namespace OmniTwin.Transport;

/// <summary>Loopback-only bounded visual-service I/O. Requests carry no model credential and never execute Unity code.</summary>
public sealed class VisualServicesClient : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private async Task<byte[]> SendAsync(Uri origin, string path, object? payload, string contentType, int maximum, CancellationToken token)
    {
        var endpoint = MediaClient.Endpoint(origin, path); var camera = path.StartsWith("api/camera/", StringComparison.Ordinal);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token); total.CancelAfter(TimeSpan.FromSeconds(camera ? 3 : 10));
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(total.Token); budget.CancelAfter(TimeSpan.FromSeconds(camera ? 1 : 3));
                try
                {
                    using var request = MediaClient.Request(payload is null ? HttpMethod.Get : HttpMethod.Post, endpoint);
                    if (payload != null) { request.Content = new ByteArrayContent(VisualWireCodec.Encode(payload)); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var failure = await MediaClient.Failure(response, budget.Token).ConfigureAwait(false);
                        if (!MediaClient.Transient(response) || attempt >= 3) throw new MediaTransportException(failure);
                        await MediaClient.Delay(response, attempt, total.Token).ConfigureAwait(false); continue;
                    }
                    if (response.Content.Headers.ContentType?.MediaType != contentType) throw new MediaTransportException("invalid_visual_content_type");
                    return await MediaClient.Bytes(response, maximum, budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { if (total.IsCancellationRequested || attempt >= 3) throw new MediaTransportException("visual_timeout"); }
                catch (Exception error) when (error is HttpRequestException || error is IOException) { if (attempt >= 3) throw new MediaTransportException("visual_network_error"); }
                await Task.Delay(200 * (1 << (attempt - 1)), total.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new MediaTransportException("visual_timeout"); }
    }
    /// <summary>Reads an atomically paired fresh camera image and mask layer; expired captures never enter the UI.</summary>
    public async Task<AnnotatedCameraPreview> ReadPreviewAsync(Uri origin, CancellationToken token)
    {
        var preview = VisualWireCodec.DecodePreview(await SendAsync(origin, "api/camera/annotated", null, "application/json", VisualWireCodec.MaximumBytes, token).ConfigureAwait(false));
        var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - preview.Image.CapturedAtUnixMs;
        if (age < -500 || age >= MediaProtocolLimits.PreviewFreshnessMilliseconds) throw new MediaTransportException("camera_preview_stale"); return preview;
    }
    /// <summary>Reads bounded semantic memory without making a model request.</summary>
    public async Task<SemanticSceneState> ReadSemanticsAsync(Uri origin, CancellationToken token) => VisualWireCodec.DecodeSemantics(await SendAsync(origin, "api/semantics/status", null, "application/json", 131072, token).ConfigureAwait(false));
    /// <summary>Sets or clears a manual tag in an exact tracking session.</summary>
    public async Task<SemanticSceneState> OverrideAsync(Uri origin, Guid sessionId, int trackId, ObjectSemantics? classification, CancellationToken token)
        => VisualWireCodec.DecodeSemantics(await SendAsync(origin, "api/semantics/override", new { sessionId, trackId, classification }, "application/json", 131072, token).ConfigureAwait(false));
    /// <summary>Queues an explicit idempotent scenery request and validates its correlation.</summary>
    public async Task<SceneryGenerationState> GenerateAsync(Uri origin, Guid requestId, string prompt, CancellationToken token)
    {
        var state = VisualWireCodec.DecodeSceneryState(await SendAsync(origin, "api/scenery/generate", new { requestId, prompt }, "application/json", 4096, token).ConfigureAwait(false));
        if (state.RequestId != requestId) throw new MediaTransportException("scenery_correlation"); return state;
    }
    /// <summary>Polls a known request without restarting generation.</summary>
    public async Task<SceneryGenerationState> ReadSceneryAsync(Uri origin, Guid requestId, CancellationToken token)
    {
        var state = VisualWireCodec.DecodeSceneryState(await SendAsync(origin, "api/scenery/status/" + requestId.ToString("D"), null, "application/json", 4096, token).ConfigureAwait(false));
        if (state.RequestId != requestId) throw new MediaTransportException("scenery_correlation"); return state;
    }
    /// <summary>Downloads only a bounded re-encoded JPEG for the requested generated surface.</summary>
    public async Task<byte[]> ReadSceneryImageAsync(Uri origin, Guid requestId, CancellationToken token)
    {
        var bytes = await SendAsync(origin, "api/scenery/image/" + requestId.ToString("D"), null, "image/jpeg", 524288, token).ConfigureAwait(false);
        var info = RasterContainer.Inspect(bytes, 1024, 524288); if (info.IsPng) throw new MediaTransportException("invalid_scenery_format"); return bytes;
    }
    /// <summary>Disposes only after the owner has cancelled and awaited all requests.</summary>
    public void Dispose() => _http.Dispose();
}
