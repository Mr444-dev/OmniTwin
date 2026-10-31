using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmniTwin.Perception.Models;

/// <summary>A bounded model error containing no prompt, image, token or provider response.</summary>
public sealed class ModelApiException : Exception
{
    /// <summary>Creates a safe operational category.</summary>
    public ModelApiException(string category) : base(category) => Category = category;
    /// <summary>Gets safe operator text.</summary>
    public string Category { get; }
}

internal sealed class MetaModelTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _owns;
    private readonly string? _key;
    private readonly Uri _endpoint;
    internal MetaModelTransport(string path, HttpClient? http, Uri? endpoint, string? key)
    {
        _endpoint = endpoint ?? new Uri("https://api.meta.ai/v1/" + path);
        if (_endpoint.UserInfo.Length != 0 || _endpoint.Query.Length != 0 || _endpoint.Fragment.Length != 0 ||
            !(_endpoint.Scheme == "https" && _endpoint.Host == "api.meta.ai" && _endpoint.IsDefaultPort) &&
            !(_endpoint.Scheme == "http" && IPAddress.TryParse(_endpoint.Host, out var address) && IPAddress.IsLoopback(address)))
            throw new ArgumentException("Use official Meta HTTPS or an owned loopback protocol fixture.");
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _owns = http is null; _key = key ?? Environment.GetEnvironmentVariable("META_API_KEY");
    }
    internal async Task<byte[]> SendAsync(object payload, int maximumBytes, TimeSpan attemptBudget, TimeSpan totalBudget, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_key) || _key.Length > 4096 || _key.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ModelApiException("auth_missing_or_invalid_401");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token); total.CancelAfter(totalBudget);
        try
        {
            for (var number = 1; ; number++)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(total.Token); attempt.CancelAfter(attemptBudget);
                var delay = TimeSpan.FromMilliseconds(300 * (1 << (number - 1)));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
                    request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var code = (int)response.StatusCode;
                        var category = code switch { 401 => "auth_missing_or_invalid_401", 403 => "permission_denied_403", 429 => "rate_limited_429", 408 or 504 => "upstream_timeout", _ => "model_upstream_error" };
                        if (number >= 3 || !(code is 408 or 429 || code >= 500)) throw new ModelApiException(category);
                        var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : delay);
                        if (retry > delay) delay = retry;
                        if (delay > TimeSpan.FromSeconds(5)) throw new ModelApiException(category);
                    }
                    else
                    {
                        if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new ModelApiException("invalid_model_response");
                        if (response.Content.Headers.ContentLength > maximumBytes) throw new ModelApiException("model_response_size");
                        await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
                        using var body = new MemoryStream(); var buffer = new byte[8192];
                        while (true)
                        {
                            var count = await stream.ReadAsync(buffer, attempt.Token).ConfigureAwait(false); if (count == 0) break;
                            if (body.Length + count > maximumBytes) throw new ModelApiException("model_response_size");
                            await body.WriteAsync(buffer.AsMemory(0, count), attempt.Token).ConfigureAwait(false);
                        }
                        return body.ToArray();
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { if (total.IsCancellationRequested || number >= 3) throw new ModelApiException("upstream_timeout"); }
                catch (Exception error) when (error is HttpRequestException or IOException)
                { if (number >= 3) throw new ModelApiException("network_error"); }
                await Task.Delay(delay, total.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new ModelApiException("upstream_timeout"); }
        finally { Array.Clear(bytes); }
    }
    public void Dispose() { if (_owns) _http.Dispose(); }
}
