using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Perception.Audio;

namespace OmniTwin.Perception.Audio;

/// <summary>A cancellable short-recording transcription boundary.</summary>
public interface IMuseVoiceClient
{
    /// <summary>Transcribes owned canonical WAV bytes; the result is an operator-editable draft.</summary>
    Task<VoiceTranscription> TranscribeAsync(Guid requestId, ReadOnlyMemory<byte> wav, CancellationToken token);
}
/// <summary>A provider failure retaining only its safe category and attempt count.</summary>
public sealed class VoiceApiException : Exception
{
    /// <summary>Creates a safe error without audio, tokens, transcript or upstream body.</summary>
    public VoiceApiException(string category, int attempts) : base(category) { Category = category; Attempts = attempts; }
    /// <summary>Gets the operator category.</summary>
    public string Category { get; }
    /// <summary>Gets attempted HTTP calls.</summary>
    public int Attempts { get; }
}
/// <summary>Real Meta file-transcription adapter: documented multipart PUSH_TO_TALK, bounded retries and strict JSON.</summary>
public sealed class MuseVoiceClient : IMuseVoiceClient, IDisposable
{
    /// <summary>Official speech-to-text model.</summary>
    public const string ModelId = "muse-voice-transcribe-1.0";
    private readonly HttpClient _http; private readonly bool _owns; private readonly string? _key; private readonly Uri _endpoint;
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Loads META_API_KEY unless an explicit owned HTTP test fixture supplies a key.</summary>
    public MuseVoiceClient(HttpClient? http = null, Uri? endpoint = null, string? apiKey = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }; _owns = http is null;
        _key = apiKey ?? Environment.GetEnvironmentVariable("META_API_KEY"); _endpoint = endpoint ?? new Uri("https://api.meta.ai/v1/asr/transcribe");
        if (!(_endpoint.Scheme == "https" && _endpoint.Host == "api.meta.ai") && !(_endpoint.Scheme == "http" && IPAddress.TryParse(_endpoint.Host, out var ip) && IPAddress.IsLoopback(ip)))
            throw new ArgumentException("Use Meta HTTPS or an owned loopback HTTP fixture.", nameof(endpoint));
    }
    /// <inheritdoc />
    public async Task<VoiceTranscription> TranscribeAsync(Guid requestId, ReadOnlyMemory<byte> wav, CancellationToken token)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Missing request identity.", nameof(requestId));
        var duration = PcmWaveCodec.Validate(wav.Span);
        if (string.IsNullOrWhiteSpace(_key) || _key.Length > 4096 || _key.Any(char.IsWhiteSpace) || _key.Any(char.IsControl)) throw new VoiceApiException("auth_missing_or_invalid_401", 0);
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) throw new VoiceApiException("voice_busy", 0);
        var owned = wav.ToArray();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token); total.CancelAfter(TimeSpan.FromSeconds(30));
        var attempts = 0;
        try
        {
            while (true)
            {
                attempts++; using var attempt = CancellationTokenSource.CreateLinkedTokenSource(total.Token); attempt.CancelAfter(TimeSpan.FromSeconds(12));
                var wait = TimeSpan.FromMilliseconds(250 * (1 << (attempts - 1)));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint + "?sessionId=" + requestId.ToString("D")));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    var form = new MultipartFormDataContent(); request.Content = form;
                    form.Add(new StringContent(JsonSerializer.Serialize(new { model = ModelId, audioEncoding = "WAV", mode = "PUSH_TO_TALK", languageBias = new[] { "Polish", "English" } }), Encoding.UTF8, "application/json"), "request");
                    var audio = new ByteArrayContent(owned); audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav"); form.Add(audio, "audio", "command.wav");
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var category = (int)response.StatusCode switch { 401 => "auth_missing_or_invalid_401", 403 => "permission_denied_403", 429 => "rate_limited_429", 408 or 504 => "upstream_timeout", _ => "voice_upstream_error" };
                        var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                        if (!transient || attempts >= 3) throw new VoiceApiException(category, attempts);
                        var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : wait);
                        if (retry > wait) wait = retry;
                        if (wait > TimeSpan.FromSeconds(5)) throw new VoiceApiException(category, attempts);
                    }
                    else
                    {
                        if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new VoiceApiException("invalid_voice_response", attempts);
                        await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
                        using var body = new MemoryStream(); var buffer = new byte[4096];
                        while (true) { var count = await stream.ReadAsync(buffer, attempt.Token).ConfigureAwait(false); if (count == 0) break; if (body.Length + count > 32768) throw new VoiceApiException("voice_response_size", attempts); await body.WriteAsync(buffer.AsMemory(0, count), attempt.Token).ConfigureAwait(false); }
                        return Parse(body.ToArray(), requestId, duration, attempts);
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { if (total.IsCancellationRequested || attempts >= 3) throw new VoiceApiException("upstream_timeout", attempts); }
                catch (Exception error) when (error is HttpRequestException or IOException)
                { if (attempts >= 3) throw new VoiceApiException("network_error", attempts); }
                await Task.Delay(wait, total.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new VoiceApiException("upstream_timeout", attempts); }
        finally { _gate.Release(); Array.Clear(owned); }
    }
    private static VoiceTranscription Parse(byte[] bytes, Guid id, int expectedDuration, int attempts)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 }); var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException(); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) if (!names.Add(property.Name)) throw new JsonException();
            if (names.Count != 4 || !names.SetEquals(new[] { "sessionId", "transcript", "audioDurationMs", "turns" }) || root.GetProperty("sessionId").GetString() != id.ToString("D")) throw new JsonException();
            var duration = root.GetProperty("audioDurationMs").GetInt32();
            if (Math.Abs((long)duration - expectedDuration) > 100 || root.GetProperty("turns").ValueKind != JsonValueKind.Array || root.GetProperty("turns").GetArrayLength() != 0) throw new JsonException();
            return new VoiceTranscription(id, root.GetProperty("transcript").GetString()!, duration);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or OverflowException or FormatException)
        { throw new VoiceApiException("invalid_voice_response", attempts); }
    }
    /// <summary>Releases the owned transport after active transcription has stopped.</summary>
    public void Dispose() { if (_owns) _http.Dispose(); _gate.Dispose(); }
}
