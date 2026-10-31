using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Perception.Audio;
using Xunit;

namespace OmniTwin.Perception.Runtime.Tests;

public sealed class MuseVoiceClientTests
{
    [Fact]
    public async Task SendsDocumentedMultipartAndCorrelatesStrictPolishTranscript()
    {
        var id = Guid.NewGuid(); var wav = PcmWaveCodec.Encode(new float[16000], 16000, 1);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("https://api.meta.ai/v1/asr/transcribe?sessionId=" + id, request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var form = Assert.IsType<MultipartFormDataContent>(request.Content); var parts = form.ToArray(); Assert.Equal(2, parts.Length);
            using var settings = JsonDocument.Parse(await parts[0].ReadAsStringAsync(token));
            Assert.Equal("muse-voice-transcribe-1.0", settings.RootElement.GetProperty("model").GetString()); Assert.Equal("PUSH_TO_TALK", settings.RootElement.GetProperty("mode").GetString());
            Assert.Equal(wav, await parts[1].ReadAsByteArrayAsync(token));
            return Reply(id, "Utwórz kordon ochronny.");
        }));
        using var client = new MuseVoiceClient(http, apiKey: "owned-voice-test-key");
        var result = await client.TranscribeAsync(id, wav, TestContext.Current.CancellationToken); Assert.Equal("Utwórz kordon ochronny.", result.Transcript);
    }
    [Theory]
    [InlineData(401, "auth_missing_or_invalid_401", 1)] [InlineData(403, "permission_denied_403", 1)]
    [InlineData(429, "rate_limited_429", 3)] [InlineData(504, "upstream_timeout", 3)]
    public async Task RetriesOnlyTransientErrorsWithoutLeakingProviderBody(int code, string category, int expected)
    {
        var calls = 0; using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("provider-private-error") }); }));
        using var client = new MuseVoiceClient(http, apiKey: "owned-voice-test-key");
        var error = await Assert.ThrowsAsync<VoiceApiException>(() => client.TranscribeAsync(Guid.NewGuid(), PcmWaveCodec.Encode(new float[16000], 16000, 1), TestContext.Current.CancellationToken));
        Assert.Equal(expected, calls); Assert.Equal(category, error.Category); Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("extra")] [InlineData("duration")] [InlineData("duration_fraction")] [InlineData("duration_overflow")] [InlineData("session")] [InlineData("turns")] [InlineData("duplicate")]
    public async Task InvalidProviderSchemaNeverProducesACommandDraft(string mutation)
    {
        var id = Guid.NewGuid(); var body = JsonSerializer.Serialize(new { sessionId = id.ToString("D"), transcript = "hold", audioDurationMs = 1000, turns = Array.Empty<object>() });
        body = mutation switch { "extra" => body.Insert(1, "\"extra\":true,"), "duration" => body.Replace("1000", "9000", StringComparison.Ordinal), "duration_fraction" => body.Replace("1000", "1000.5", StringComparison.Ordinal), "duration_overflow" => body.Replace("1000", "1e99", StringComparison.Ordinal), "session" => body.Replace(id.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal), "turns" => body.Replace("[]", "[{}]", StringComparison.Ordinal), _ => body.Insert(1, "\"transcript\":\"other\",") };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") })));
        using var client = new MuseVoiceClient(http, apiKey: "owned-voice-test-key");
        Assert.Equal("invalid_voice_response", (await Assert.ThrowsAsync<VoiceApiException>(() => client.TranscribeAsync(id, PcmWaveCodec.Encode(new float[16000], 16000, 1), TestContext.Current.CancellationToken))).Category);
    }
    [Fact]
    public async Task CancellationInterruptsBodyAndMissingKeyMakesNoRequest()
    {
        var calls = 0; using var http = new HttpClient(new Handler(async (_, token) => { calls++; await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }));
        var wav = PcmWaveCodec.Encode(new float[16000], 16000, 1);
        using (var missing = new MuseVoiceClient(http, apiKey: "")) Assert.Equal("auth_missing_or_invalid_401", (await Assert.ThrowsAsync<VoiceApiException>(() => missing.TranscribeAsync(Guid.NewGuid(), wav, TestContext.Current.CancellationToken))).Category);
        Assert.Equal(0, calls); using var client = new MuseVoiceClient(http, apiKey: "owned-voice-test-key"); using var stop = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TranscribeAsync(Guid.NewGuid(), wav, stop.Token)); Assert.Equal(1, calls);
    }
    private static HttpResponseMessage Reply(Guid id, string text) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { sessionId = id.ToString("D"), transcript = text, audioDurationMs = 1000, turns = Array.Empty<object>() }), Encoding.UTF8, "application/json") };
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _send(request, cancellationToken);
    }
}
