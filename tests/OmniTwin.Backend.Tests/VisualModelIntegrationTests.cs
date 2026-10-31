using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniTwin.Perception.Imaging;
using OmniTwin.Perception.Models;
using OmniTwin.Contracts;
using OmniTwin.Transport;
using OpenCvSharp;
using Xunit;

namespace OmniTwin.Backend.Tests;

public sealed class VisualModelIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MuseSparkSendsRealBoundedRoiAndAcceptsOnlyConstrainedSemantics()
    {
        using var image = new Mat(36, 48, MatType.CV_8UC3, new Scalar(30, 70, 110));
        Assert.True(Cv2.ImEncode(".jpg", image, out var crop));
        await using var provider = await Provider.StartAsync(async context =>
        {
            Assert.Equal("Bearer owned-meta-fixture-key", context.Request.Headers.Authorization.ToString());
            using var request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            Assert.Equal(MuseSparkSemanticClient.ModelId, request.RootElement.GetProperty("model").GetString());
            var parts = request.RootElement.GetProperty("messages")[1].GetProperty("content");
            Assert.Contains("data:image/jpeg;base64," + Convert.ToBase64String(crop), parts[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("json_schema", request.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(SparkReply("{\"label\":\"cup\",\"role\":\"obstacle\",\"hazardLevel\":\"none\",\"confidence\":0.91}"), context.RequestAborted);
        });

        using var http = new HttpClient(); using var client = new MuseSparkSemanticClient(http, provider.Endpoint("/v1/chat/completions"), "owned-meta-fixture-key");
        var semantics = await client.ClassifyAsync(crop, Token);
        Assert.Equal("cup", semantics.Label); Assert.Equal("obstacle", semantics.Role); Assert.Equal("none", semantics.HazardLevel);
        Assert.Equal(0.91f, semantics.Confidence);
    }

    [Fact]
    public async Task MuseImageAcceptsPngAndReturnsLowContrastValidatedJpegOnly()
    {
        using var generated = new Mat(48, 64, MatType.CV_8UC3);
        var rows = generated.Rows; var columns = generated.Cols;
        for (var y = 0; y < rows; y++) for (var x = 0; x < columns; x++)
            generated.Set(y, x, new Vec3b((byte)((x * 13 + y * 3) % 256), (byte)((y * 17 + 40) % 256), (byte)((x * 7 + 120) % 256)));
        Assert.True(Cv2.ImEncode(".png", generated, out var png));
        await using var provider = await Provider.StartAsync(async context =>
        {
            Assert.Equal("Bearer owned-meta-fixture-key", context.Request.Headers.Authorization.ToString());
            using var request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var body = request.RootElement;
            Assert.Equal(MuseImageClient.ModelId, body.GetProperty("model").GetString());
            Assert.Equal("b64_json", body.GetProperty("response_format").GetString());
            Assert.Equal("png", body.GetProperty("output_format").GetString());
            Assert.False(body.GetProperty("tool_enablement").GetProperty("enable_shell").GetBoolean());
            Assert.False(body.GetProperty("tool_enablement").GetProperty("enable_web_search").GetBoolean());
            Assert.False(body.GetProperty("tool_enablement").GetProperty("enable_image_search").GetBoolean());
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { created = 1, data = new[] { new { b64_json = Convert.ToBase64String(png) } }, output_format = "png" }, context.RequestAborted);
        });

        using var http = new HttpClient(); using var client = new MuseImageClient(http, provider.Endpoint("/v1/images/generations"), "owned-meta-fixture-key");
        var result = await client.GenerateAsync("muted sci-fi grid", Token);
        var info = RasterContainer.Inspect(result, 1024, 524288);
        Assert.False(info.IsPng); Assert.Equal(64, info.Width); Assert.Equal(48, info.Height);
        using var decoded = Cv2.ImDecode(result, ImreadModes.Color); using var channels = decoded.Reshape(1);
        Cv2.MinMaxLoc(channels, out double minimum, out double maximum);
        Assert.InRange(maximum - minimum, 1, 90);
    }

    [Theory]
    [InlineData(401, "auth_missing_or_invalid_401")]
    [InlineData(403, "permission_denied_403")]
    public async Task MetaErrorsAreCategorizedWithoutExposingBodies(int status, string category)
    {
        await using var provider = await Provider.StartAsync(async context =>
        {
            context.Response.StatusCode = status; await context.Response.WriteAsync("private-token-shaped-upstream-body", context.RequestAborted);
        });
        using var http = new HttpClient(); using var client = new MuseImageClient(http, provider.Endpoint("/v1/images/generations"), "owned-meta-fixture-key");
        var error = await Assert.ThrowsAsync<ModelApiException>(() => client.GenerateAsync("small neutral grid", Token));
        Assert.Equal(category, error.Category); Assert.DoesNotContain("private-token-shaped", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MuseImageRejectsUrlOnlyResponsesAndMalformedCropsBeforeNetwork()
    {
        await using var provider = await Provider.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { created = 1, data = new[] { new { url = "https://untrusted.invalid/image.png" } }, output_format = "png" }, context.RequestAborted);
        });
        using var http = new HttpClient(); using var client = new MuseImageClient(http, provider.Endpoint("/v1/images/generations"), "owned-meta-fixture-key");
        Assert.Equal("invalid_generated_image", (await Assert.ThrowsAsync<ModelApiException>(() => client.GenerateAsync("grid", Token))).Category);
        var imageClient = new MuseSparkSemanticClient(http, provider.Endpoint("/v1/chat/completions"), "owned-meta-fixture-key");
        await Assert.ThrowsAsync<InvalidDataException>(() => imageClient.ClassifyAsync(new byte[] { 1, 2, 3, 4 }, Token));
    }

    [Fact]
    public void AnnotatedPipRequiresCurrentImageDimensionsAndUnrenewedSamProvenance()
    {
        using var color = new Mat(30, 40, MatType.CV_8UC3, new Scalar(40, 60, 80));
        using var alpha = new Mat(30, 40, MatType.CV_8UC4, Scalar.All(0));
        Assert.True(Cv2.ImEncode(".jpg", color, out var jpeg)); Assert.True(Cv2.ImEncode(".png", alpha, out var png));
        var session = Guid.NewGuid(); var calibration = Guid.NewGuid(); var tracking = Guid.NewGuid();
        var captured = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var image = new CameraPreview(session, calibration, 8, captured, 40, 30, jpeg);
        var local = new PipMaskMetadata(3, 0.91f, 0.8f, true, ObservationSource.LocalMotionEstimate, 7, captured - 100);
        var paired = new AnnotatedCameraPreview(image, tracking, png, new[] { local }, 4);
        Assert.Equal(ObservationSource.LocalMotionEstimate, Assert.Single(paired.Masks).ObservationSource);
        Assert.Equal(7, paired.Masks[0].SamSequenceNumber); Assert.Equal(8, paired.Image.SequenceNumber);
        Assert.Throws<WorldStreamProtocolException>(() => new AnnotatedCameraPreview(image, tracking, png,
            new[] { new PipMaskMetadata(3, 0.91f, 0.8f, true, ObservationSource.SamVerified, 7, captured - 100) }));
        Assert.Throws<WorldStreamProtocolException>(() => new AnnotatedCameraPreview(image, tracking, new byte[] { 1, 2, 3, 4 }, Array.Empty<PipMaskMetadata>()));

        var wire = VisualWireCodec.Encode(new
        {
            schemaVersion = 1, sessionId = session, calibrationId = calibration, sequenceNumber = 8L,
            capturedAtUnixMs = captured, width = 40, height = 30, jpeg = Convert.ToBase64String(jpeg), trackingSessionId = tracking,
            maskLayer = Convert.ToBase64String(png), masks = new[] { new { trackId = 3, confidence = 0.91f, trackingQuality = 0.8f,
                isHazardZone = true, observationSource = (int)ObservationSource.LocalMotionEstimate, samSequenceNumber = 7L, samCapturedAtUnixMs = captured - 100 } },
            overlayRevision = 4L
        });
        Assert.Equal(ObservationSource.LocalMotionEstimate, Assert.Single(VisualWireCodec.DecodePreview(wire).Masks).ObservationSource);
    }

    internal static string SparkReply(string content) => JsonSerializer.Serialize(new
    {
        id = "fixture", @object = "chat.completion", created = 1, model = MuseSparkSemanticClient.ModelId,
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } }
    });

    internal sealed class Provider : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly Uri _root;
        private Provider(WebApplication app, Uri root) { _app = app; _root = root; }
        internal Uri Endpoint(string path) => new(_root, path);
        internal static async Task<Provider> StartAsync(Func<HttpContext, Task> response)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build(); app.MapPost("/{**path}", response); await app.StartAsync(Token);
            var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
            return new Provider(app, new Uri(address));
        }
        public async ValueTask DisposeAsync()
        { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await _app.StopAsync(stop.Token); await _app.DisposeAsync(); }
    }
}
