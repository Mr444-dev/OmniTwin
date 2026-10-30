using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Perception.Vision;
using OpenCvSharp;
using Xunit;

namespace OmniTwin.Perception.Runtime.Tests;

public sealed class Sam31ClientTests
{
    private static CapturedFrame Frame() => new(42, DateTimeOffset.UtcNow, 2, 3,
        new byte[] { 255, 216, 255, 217 }, new FrameChangeDecision(1, true));

    internal static string Geometry()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sam-one-bit.json")));
        var fixture = document.RootElement.EnumerateArray().Single(item => item.GetProperty("name").GetString() == "2x3-one");
        return "<0f>7<|box;x1=0;y1=0;x2=1;y2=2;w=2;h=3;c=0.9|><|mask;x=0;y=0;c=0.9;data=3,2," +
            fixture.GetProperty("payload").GetString() + "|>";
    }

    internal static string Body(string? geometry = null, string status = "completed") => JsonSerializer.Serialize(new
    {
        id = "resp_fixture", @object = "response", model = "sam-3.1", status,
        output = new[] { new { type = "message", role = "assistant", status = "completed",
            content = new[] { new { type = "output_text", text = geometry ?? Geometry() } } } }
    });

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static SamClientOptions FastOptions(int attempts = 3) => new()
    { InitialRetryDelay = TimeSpan.Zero, MaximumAttempts = attempts };

    [Fact]
    public async Task SendsDocumentedRequestAndDecodesRealWireGeometry()
    {
        using var transport = new HttpClient(new Handler(async (request, _) =>
        {
            Assert.Equal("https://api.meta.ai/v1/responses", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("sam-3.1", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("true", body.RootElement.GetProperty("metadata").GetProperty("include_confidence").GetString());
            Assert.Equal("one_bit", body.RootElement.GetProperty("metadata").GetProperty("mask_encoding").GetString());
            return JsonResponse(Body());
        }));
        using var client = new Sam31Client(transport, "fixture-key", FastOptions());
        var result = await client.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken);
        Assert.Equal(42, result.SequenceNumber);
        var detection = Assert.Single(result.Detections);
        Assert.Equal(7, detection.ObjectId);
        Assert.Equal(6, detection.ForegroundPixelCount);
        Assert.True(detection.IsForeground(1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => detection.IsForeground(2, 0));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task RetriesTransientErrorsThenSucceeds(int status)
    {
        var calls = 0;
        using var transport = new HttpClient(new Handler((_, _) => Task.FromResult(++calls < 3 ?
            new HttpResponseMessage((HttpStatusCode)status) : JsonResponse(Body("")))));
        using var client = new Sam31Client(transport, "fixture-key", FastOptions());
        Assert.Empty((await client.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken)).Detections);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(302)]
    public async Task DoesNotRetryPermanentHttpFailures(int status)
    {
        var calls = 0;
        using var transport = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)); }));
        using var client = new Sam31Client(transport, "fixture-key", FastOptions());
        var error = await Assert.ThrowsAsync<SamApiException>(() => client.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken));
        Assert.Equal("http_error", error.Category);
        Assert.Equal(1, calls);
        Assert.Equal(1, error.Attempts);
    }

    [Fact]
    public async Task HonorsLongRetryAfterByFailingBeforeDeadlineInsteadOfRetryingEarly()
    {
        var calls = 0;
        using var transport = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        }));
        using var client = new Sam31Client(transport, "fixture-key", FastOptions());
        var error = await Assert.ThrowsAsync<SamApiException>(() => client.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken));
        Assert.Equal("retry_exceeds_deadline", error.Category);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task StopsAfterConfiguredTransientAttempts()
    {
        var calls = 0;
        using var transport = new HttpClient(new Handler((_, _) => { calls++; throw new HttpRequestException("fixture-network-failure"); }));
        using var client = new Sam31Client(transport, "fixture-key", FastOptions());
        var error = await Assert.ThrowsAsync<SamApiException>(() => client.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken));
        Assert.Equal("network_error", error.Category);
        Assert.Equal(3, calls);
        Assert.DoesNotContain("fixture-key", error.ToString());
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    public async Task RejectsIncompleteEnvelopeWithoutRetry(string status)
    {
        var calls = 0;
        using var transport = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(JsonResponse(Body(status: status))); }));
        using var client = new Sam31Client(transport, "fixture-key", FastOptions());
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<SamApiException>(() => client.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken))).Category);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RejectsAmbiguousJsonAndGeometry()
    {
        var bodies = new[]
        {
            Body().Replace("\"status\":\"completed\"", "\"status\":\"completed\",\"status\":\"completed\""),
            Body().Replace("sam-3.1", "wrong-model"),
            Body(Geometry().Replace("x2=1", "x2=2")),
            Body(Geometry().Replace("c=0.9;data", "c=0.1;data")),
            Body(Geometry() + "," + Geometry().Substring(4)),
            Body(Geometry().Replace("data=3,2,!", "data=3,2,~")),
            Body(Geometry().Replace("<0f>", "<1f>")),
            Body("unexpected prose"),
            "{}"
        };
        foreach (var body in bodies)
        {
            using var transport = new HttpClient(new Handler((_, _) => Task.FromResult(JsonResponse(body))));
            using var client = new Sam31Client(transport, "fixture-key", FastOptions());
            Assert.Equal("invalid_response", (await Assert.ThrowsAsync<SamApiException>(() => client.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken))).Category);
        }
    }

    [Fact]
    public async Task RejectsOversizedAndWrongContentTypeResponses()
    {
        using var largeTransport = new HttpClient(new Handler((_, _) => Task.FromResult(JsonResponse(new string('x', 1024)))));
        using var largeClient = new Sam31Client(largeTransport, "fixture-key", new SamClientOptions { MaximumResponseBytes = 256 });
        Assert.Equal("response_too_large", (await Assert.ThrowsAsync<SamApiException>(() => largeClient.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken))).Category);
        using var htmlTransport = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<html>fixture</html>") })));
        using var htmlClient = new Sam31Client(htmlTransport, "fixture-key");
        Assert.Equal("invalid_content_type", (await Assert.ThrowsAsync<SamApiException>(() => htmlClient.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken))).Category);
    }

    [Fact]
    public async Task EnforcesBodyReadDeadlineAndStreamingSizeLimit()
    {
        using var slowTransport = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }));
        using var slow = new Sam31Client(slowTransport, "fixture-key", new SamClientOptions
        { MaximumAttempts = 1, AttemptTimeout = TimeSpan.FromMilliseconds(30) });
        Assert.Equal("attempt_timeout", (await Assert.ThrowsAsync<SamApiException>(() => slow.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken))).Category);
        using var largeTransport = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new byte[1024])) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }));
        using var large = new Sam31Client(largeTransport, "fixture-key", new SamClientOptions { MaximumResponseBytes = 256 });
        Assert.Equal("response_too_large", (await Assert.ThrowsAsync<SamApiException>(() => large.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken))).Category);
    }

    [Fact]
    public async Task RetriesBrokenBodyStreamsWithinAttemptLimit()
    {
        using var transport = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }));
        using var client = new Sam31Client(transport, "fixture-key", FastOptions());
        var error = await Assert.ThrowsAsync<SamApiException>(() => client.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken));
        Assert.Equal("network_error", error.Category);
        Assert.Equal(3, error.Attempts);
    }

    [Fact]
    public async Task CancelsCallerWithoutRetryAndRejectsOverlappingRequests()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var transport = new HttpClient(new Handler(async (_, token) =>
        {
            calls++; entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return JsonResponse(Body());
        }));
        using var client = new Sam31Client(transport, "fixture-key");
        using var stop = new CancellationTokenSource();
        var first = client.SegmentAsync(Frame(), "cup", stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RetriesAttemptTimeoutsAndClassifiesTotalTimeout()
    {
        using var transport = new HttpClient(new Handler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return JsonResponse(Body()); }));
        using var attempts = new Sam31Client(transport, "fixture-key", new SamClientOptions
        { AttemptTimeout = TimeSpan.FromMilliseconds(20), TotalTimeout = TimeSpan.FromSeconds(5), InitialRetryDelay = TimeSpan.Zero });
        var error = await Assert.ThrowsAsync<SamApiException>(() => attempts.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken));
        Assert.Equal("attempt_timeout", error.Category);
        Assert.Equal(3, error.Attempts);
        using var total = new Sam31Client(transport, "fixture-key", new SamClientOptions
        { AttemptTimeout = TimeSpan.FromMilliseconds(30), TotalTimeout = TimeSpan.FromMilliseconds(30), InitialRetryDelay = TimeSpan.Zero });
        Assert.Equal("total_timeout", (await Assert.ThrowsAsync<SamApiException>(() => total.SegmentAsync(Frame(), "cup", TestContext.Current.CancellationToken))).Category);
    }

    [Fact]
    public void NativeJpegProcessingValidatesDimensionsAndOwnsData()
    {
        using var mat = new Mat(12, 16, MatType.CV_8UC3, new Scalar(20, 40, 60));
        Cv2.ImEncode(".jpg", mat, out var bytes);
        var frame = CapturedFrame.FromJpeg(bytes);
        Assert.Equal(16, frame.Width);
        Assert.Equal(12, frame.Height);
        bytes[0] = 0;
        var copy = frame.CopyJpeg();
        Assert.Equal(255, copy[0]);
        copy[0] = 0;
        Assert.Equal(255, frame.CopyJpeg()[0]);
        Assert.Throws<InvalidDataException>(() => CapturedFrame.FromJpeg(bytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraCaptureWorker(-1));
    }

    [Fact]
    public void NormalizesExifOrientationBeforeSendingJpeg()
    {
        using var mat = new Mat(12, 16, MatType.CV_8UC3, new Scalar(20, 40, 60));
        Cv2.ImEncode(".jpg", mat, out var jpeg);
        var exif = new byte[] { 255, 225, 0, 34, 69, 120, 105, 102, 0, 0,
            73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0 };
        var oriented = new byte[jpeg.Length + exif.Length];
        Array.Copy(jpeg, 0, oriented, 0, 2);
        Array.Copy(exif, 0, oriented, 2, exif.Length);
        Array.Copy(jpeg, 2, oriented, 2 + exif.Length, jpeg.Length - 2);
        var frame = CapturedFrame.FromJpeg(oriented);
        Assert.Equal(12, frame.Width);
        Assert.Equal(16, frame.Height);
        using var decoded = Cv2.ImDecode(frame.CopyJpeg(), ImreadModes.Color);
        Assert.Equal(frame.Width, decoded.Width);
        Assert.Equal(frame.Height, decoded.Height);
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        internal NonSeekableStream(byte[] bytes) : base(bytes) { }
        public override bool CanSeek => false;
    }

    private sealed class StallingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("fixture-broken-stream");
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => _send(request, token);
    }
}
