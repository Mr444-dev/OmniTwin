using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmniTwin.Perception.Vision;

/// <summary>Segments one owned image asynchronously using the documented SAM Responses protocol.</summary>
public interface ISam31Client
{
    /// <summary>Segments one noun phrase; caller cancellation propagates without retry.</summary>
    Task<SamSegmentationResult> SegmentAsync(CapturedFrame frame, string nounPhrase, CancellationToken cancellationToken = default);
}

/// <summary>Bounded transport settings; defaults allow three attempts within one minute.</summary>
public sealed class SamClientOptions
{
    /// <summary>Gets or initializes the maximum number of HTTP attempts, from one through five.</summary>
    public int MaximumAttempts { get; init; } = 3;
    /// <summary>Gets or initializes the timeout for headers and body of one request.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Gets or initializes the total operation deadline including backoff.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Gets or initializes the initial exponential retry delay.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Gets or initializes the maximum accepted JSON body size.</summary>
    public int MaximumResponseBytes { get; init; } = 8_388_608;

    internal void Validate()
    {
        if (MaximumAttempts < 1 || MaximumAttempts > 5 || AttemptTimeout <= TimeSpan.Zero ||
            TotalTimeout < AttemptTimeout || TotalTimeout > TimeSpan.FromMinutes(5) ||
            InitialRetryDelay < TimeSpan.Zero || InitialRetryDelay > TimeSpan.FromSeconds(5) ||
            MaximumResponseBytes < 256 || MaximumResponseBytes > 33_554_432)
            throw new ArgumentException("Invalid SAM timeout, retry, or response limit settings.");
    }
}

/// <summary>A sanitized transport or validation failure without credentials, image data, or provider body text.</summary>
public sealed class SamApiException : Exception
{
    /// <summary>Creates a structured client failure.</summary>
    public SamApiException(string category, int attempts, HttpStatusCode? statusCode = null)
        : base($"SAM operation failed: {category}; attempts={attempts}; status={(int?)statusCode}.")
    {
        Category = category; Attempts = attempts; StatusCode = statusCode;
    }
    /// <summary>Gets the stable failure category.</summary>
    public string Category { get; }
    /// <summary>Gets the number of HTTP attempts made.</summary>
    public int Attempts { get; }
    /// <summary>Gets the HTTP status if available.</summary>
    public HttpStatusCode? StatusCode { get; }
}

/// <summary>Non-streaming still-image SAM adapter with validation, bounded retry, deadlines, and no overlapping calls.</summary>
public sealed class Sam31Client : ISam31Client, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly SamClientOptions _options;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private int _disposed;

    /// <summary>Uses an injected HTTP client and environment-sourced key. The caller owns the HTTP client.</summary>
    /// <remarks>The handler must disable redirects; redirects are rejected instead of followed by the environment factory.</remarks>
    public Sam31Client(HttpClient http, string apiKey, SamClientOptions? options = null)
        : this(http, apiKey, options, false) { }

    private Sam31Client(HttpClient http, string apiKey, SamClientOptions? options, bool ownsHttp)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            throw new ArgumentException("A valid environment-sourced API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _options = options ?? new SamClientOptions();
        _options.Validate();
        _ownsHttp = ownsHttp;
    }

    /// <summary>Loads META_API_KEY and creates an owned, nonredirecting HTTP transport.</summary>
    public static Sam31Client FromEnvironment(SamClientOptions? options = null)
    {
        var key = Environment.GetEnvironmentVariable("META_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Set META_API_KEY in the process environment.");
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip })
        { Timeout = Timeout.InfiniteTimeSpan };
        try { return new Sam31Client(http, key, options, true); }
        catch { http.Dispose(); throw; }
    }

    /// <inheritdoc />
    public async Task<SamSegmentationResult> SegmentAsync(CapturedFrame frame, string nounPhrase, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (frame is null) throw new ArgumentNullException(nameof(frame));
        if (string.IsNullOrWhiteSpace(nounPhrase) || nounPhrase.Length > 128 ||
            nounPhrase.IndexOfAny(new[] { '\r', '\n', '<', '>', ',', ';' }) >= 0)
            throw new ArgumentException("Use one noun phrase of at most 128 characters.", nameof(nounPhrase));
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("This client already has an active segmentation request.");
        var attempts = 0;
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.TotalTimeout);
        try
        {
            var requestBody = JsonSerializer.SerializeToUtf8Bytes(new
            {
                model = "sam-3.1", stream = false,
                input = new[] { new { type = "message", role = "user", content = new object[]
                {
                    new { type = "input_text", text = nounPhrase.Trim() },
                    new { type = "input_image", image_url = "data:image/jpeg;base64," + Convert.ToBase64String(frame.Jpeg) }
                } } },
                metadata = new { mask_encoding = "one_bit", include_confidence = "true" }
            });
            while (true)
            {
                attempts++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(5000,
                    _options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempts - 1)));
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(_options.AttemptTimeout);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.meta.ai/v1/responses");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                    request.Content = new ByteArrayContent(requestBody);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        var mediaType = response.Content.Headers.ContentType?.MediaType;
                        if (mediaType != "application/json") throw new SamApiException("invalid_content_type", attempts, response.StatusCode);
                        var body = await ReadBoundedAsync(response.Content, attempts, attempt.Token).ConfigureAwait(false);
                        try
                        {
                            var parsed = SamResponseParser.Parse(body, frame.Width, frame.Height);
                            deadline.Token.ThrowIfCancellationRequested();
                            return new SamSegmentationResult(parsed.Id, frame, clock.Elapsed.TotalMilliseconds, parsed.Detections);
                        }
                        catch (Exception validation) when (validation is FormatException || validation is JsonException || validation is InvalidOperationException)
                        { throw new SamApiException("invalid_response", attempts, response.StatusCode); }
                    }
                    if (!IsTransient(response.StatusCode) || attempts >= _options.MaximumAttempts)
                        throw new SamApiException("http_error", attempts, response.StatusCode);
                    var retryAfter = response.Headers.RetryAfter;
                    var advised = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
                    if (advised.HasValue && advised.Value > delay) delay = advised.Value;
                }
                catch (HttpRequestException)
                {
                    if (attempts >= _options.MaximumAttempts) throw new SamApiException("network_error", attempts);
                }
                catch (IOException)
                {
                    if (attempts >= _options.MaximumAttempts) throw new SamApiException("network_error", attempts);
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                    if (attempts >= _options.MaximumAttempts) throw new SamApiException("attempt_timeout", attempts);
                }
                var remaining = _options.TotalTimeout - clock.Elapsed;
                if (deadline.IsCancellationRequested || remaining <= TimeSpan.Zero) throw new SamApiException("total_timeout", attempts);
                if (delay >= remaining) throw new SamApiException("retry_exceeds_deadline", attempts);
                await Task.Delay(delay, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new SamApiException("total_timeout", attempts); }
        finally { _gate.Release(); }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, int attempts, CancellationToken token)
    {
        if (content.Headers.ContentLength > _options.MaximumResponseBytes) throw new SamApiException("response_too_large", attempts);
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (result.Length + count > _options.MaximumResponseBytes) throw new SamApiException("response_too_large", attempts);
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private static bool IsTransient(HttpStatusCode code) => code is HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>Disposes only an environment-created transport. Await active operations before disposing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsHttp) _http.Dispose();
    }
}
