using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniTwin.Backend;
using OmniTwin.Perception.Tracking;
using Xunit;

namespace OmniTwin.Backend.Tests;

public sealed class CalibrationEndpointTests
{
    private const string Profile = """
        {"schemaVersion":1,"calibrationId":"445c047b-81fa-43e9-a110-c792694a7fe3","imageWidth":640,"imageHeight":480,
        "cameraCorners":[{"u":10,"v":20},{"u":620,"v":30},{"u":600,"v":450},{"u":20,"v":440}],
        "worldCorners":[{"x":-10,"z":-6.25},{"x":10,"z":-6.25},{"x":10,"z":6.25},{"x":-10,"z":6.25}],
        "minimumRadiusMeters":0.05,"maximumRadiusMeters":3,"safetyMarginMeters":0.1,"minimumConfidence":0.3}
        """;

    [Theory]
    [InlineData("/calibration", "text/html")]
    [InlineData("/calibration/calibration.css", "text/css")]
    [InlineData("/calibration/calibration.js", "text/javascript")]
    public async Task EmbeddedAssetsHaveLocalOnlySecurityPolicy(string path, string contentType)
    {
        await using var server = await Server.StartAsync();
        using var response = await server.Http.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidProfileReturnsIdenticalValidatedAttachmentAndDoesNotEnablePerception()
    {
        await using var server = await Server.StartAsync();
        using var request = Request(Profile, server.Http.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var response = await server.Http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("calibration.json", response.Content.Headers.ContentDisposition.FileName);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Profile, Encoding.UTF8.GetString(bytes));
        Assert.Equal(640, CalibrationProfile.Parse(bytes).ImageWidth);
        Assert.Contains("disabled", await server.Http.GetStringAsync("/api/status", TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await server.Http.GetAsync("/api/world/latest", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("")]
    public async Task InvalidBodiesAreRejected(string body)
    {
        await using var server = await Server.StartAsync();
        using var request = Request(body);
        using var response = await server.Http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_calibration", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("outside")]
    [InlineData("collapsed")]
    [InlineData("version")]
    public async Task ClosedSchemaAndGeometricFailuresCannotBeExported(string mutation)
    {
        var body = mutation switch
        {
            "duplicate" => Profile.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            "unknown" => Profile.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"extra\":0", StringComparison.Ordinal),
            "outside" => Profile.Replace("\"u\":620", "\"u\":640", StringComparison.Ordinal),
            "collapsed" => Profile.Replace("\"u\":620,\"v\":30", "\"u\":10,\"v\":20", StringComparison.Ordinal),
            _ => Profile.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal)
        };
        await using var server = await Server.StartAsync();
        using var request = Request(body);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Http.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://127.0.0.1:1")]
    public async Task ForeignOriginsAreDenied(string origin)
    {
        await using var server = await Server.StartAsync();
        using var request = Request(Profile, origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Http.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task JsonHeaderAndPostAreRequired()
    {
        await using var server = await Server.StartAsync();
        using var missingHeader = Request(Profile); missingHeader.Headers.Remove("X-OmniTwin-Calibration");
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Http.SendAsync(missingHeader, TestContext.Current.CancellationToken)).StatusCode);
        using var wrongType = Request(Profile); wrongType.Content!.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await server.Http.SendAsync(wrongType, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await server.Http.GetAsync("/api/calibration/validate", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SizeLimitAppliesToKnownAndChunkedBodies(bool chunked)
    {
        await using var server = await Server.StartAsync();
        using var request = Request(new string(' ', 16_385));
        if (chunked) request.Content = new ChunkedContent(new string(' ', 16_385));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await server.Http.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
    }

    private static HttpRequestMessage Request(string body, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/calibration/validate") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-OmniTwin-Calibration", "1");
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task IncompleteBodyTimesOutInsteadOfHoldingTheRequestIndefinitely()
    {
        await using var server = await Server.StartAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, server.Http.BaseAddress!.Port, deadline.Token);
        await using var stream = connection.GetStream();
        var request = $"POST /api/calibration/validate HTTP/1.1\r\nHost: {server.Http.BaseAddress.Authority}\r\nContent-Type: application/json\r\nX-OmniTwin-Calibration: 1\r\nTransfer-Encoding: chunked\r\n\r\n1\r\n{{\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), deadline.Token);
        var bytes = new byte[1024];
        var count = await stream.ReadAsync(bytes, deadline.Token);
        Assert.StartsWith("HTTP/1.1 408", Encoding.ASCII.GetString(bytes, 0, count));
    }

    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _bytes;
        internal ChunkedContent(string text) { _bytes = Encoding.UTF8.GetBytes(text); Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly WebApplication _app;
        internal HttpClient Http { get; }
        private Server(WebApplication app, Uri address) { _app = app; Http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) }; }
        internal static async Task<Server> StartAsync()
        {
            var app = BackendApplication.Create(Array.Empty<string>(), builder =>
            {
                builder.Configuration["OmniTwin:Port"] = "0";
                builder.Configuration["OmniTwin:EnablePerception"] = "false";
                builder.Logging.ClearProviders();
            });
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Server(app, new Uri(address));
        }
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(deadline.Token); await _app.DisposeAsync();
        }
    }
}
