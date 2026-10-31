using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using OmniTwin.Backend.Media;
using OmniTwin.Contracts;
using OmniTwin.Perception.Models;
using OmniTwin.Perception.Tracking;
using OmniTwin.Perception.Vision;
using OpenCvSharp;
using Xunit;

namespace OmniTwin.Backend.Tests;

public sealed class ObjectSemanticServiceTests
{
    private const string ModelClassification = "{\"label\":\"chemical drum\",\"role\":\"hazard\",\"hazardLevel\":\"high\",\"confidence\":0.88}";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RequiresTwoStableSamObservationsAndKeepsManualOverrideAheadOfModel()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = await VisualModelIntegrationTests.Provider.StartAsync(async context =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(context.RequestAborted);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(VisualModelIntegrationTests.SparkReply(ModelClassification), context.RequestAborted);
        });
        using var http = new HttpClient(); using var client = new MuseSparkSemanticClient(http, provider.Endpoint("/v1/chat/completions"), "owned-meta-fixture-key");
        using var service = new ObjectSemanticService(client);
        await service.StartAsync(CancellationToken.None);
        var session = Guid.NewGuid(); var calibration = Guid.NewGuid(); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var first = Batch(session, 7, now); service.Observe(first);
        Assert.Equal("confirming", Assert.Single(service.Read().Objects).Status);
        service.Observe(Batch(session, 8, now + 600));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        var manual = new ObjectSemantics("Operator cleared", "obstacle", "none", 1);
        Assert.True(service.Override(session, 42, manual)); Assert.Same(manual, service.Resolve(42));
        release.TrySetResult(); await Until(() => Assert.Single(service.Read().Objects).Status == "ready");
        var row = Assert.Single(service.Read().Objects);
        Assert.True(row.OperatorOverride); Assert.Same(manual, row.Classification);
        Assert.Equal("Operator cleared", service.Resolve(42)!.Label);

        Assert.True(service.Override(session, 42, null));
        var model = service.Resolve(42); Assert.NotNull(model); Assert.Equal("chemical drum", model.Label); Assert.True(model.IsHazardZone);
        var obstacle = new SegmentedObstacle(42, "unknown", new Vector2D(1.5f, -2), 0.7f, 0.9f, false);
        var track = new TrackedObstacleState(obstacle, now, 350, 0.7f, false, ObservationSource.SamVerified, 350, now - 350);
        var world = new ObstacleTrackingSnapshot(session, calibration, 8, 8, 350, true, now + 600, new[] { track });
        var decorated = Assert.Single(service.Decorate(world).Obstacles);
        Assert.Equal("chemical drum", decorated.Obstacle.Label); Assert.True(decorated.Obstacle.IsHazardZone);
        Assert.Equal(new Vector2D(1.5f, -2), decorated.Obstacle.WorldPosition); Assert.Equal(0.7f, decorated.Obstacle.RadiusMeters);
        Assert.Equal(350, decorated.GeometryAgeMilliseconds); Assert.Equal(now - 350, decorated.GeometryObservedUnixMs);
        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }

    [Fact]
    public async Task SessionChangeCancelsAnInFlightCropClassificationAndDropsLateResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = await VisualModelIntegrationTests.Provider.StartAsync(async context =>
        {
            entered.TrySetResult();
            try
            {
                await release.Task.WaitAsync(context.RequestAborted); context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(VisualModelIntegrationTests.SparkReply(ModelClassification), context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        using var http = new HttpClient(); using var client = new MuseSparkSemanticClient(http, provider.Endpoint("/v1/chat/completions"), "owned-meta-fixture-key");
        using var service = new ObjectSemanticService(client);
        await service.StartAsync(CancellationToken.None);
        var oldSession = Guid.NewGuid(); var newSession = Guid.NewGuid(); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        service.Observe(Batch(oldSession, 1, now)); service.Observe(Batch(oldSession, 2, now + 600));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        service.Observe(Batch(newSession, 1, now + 1000));
        release.TrySetResult(); await Until(() => service.Read().SessionId == newSession);
        await Task.Delay(100, Token);
        var state = service.Read(); Assert.Equal(newSession, state.SessionId);
        Assert.Equal("confirming", Assert.Single(state.Objects).Status); Assert.Null(service.Resolve(42));
        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }

    private static VerifiedMaskBatch Batch(Guid session, long sequence, long capturedAt)
    {
        using var image = new Mat(48, 64, MatType.CV_8UC3, new Scalar(20, 40, 60));
        Assert.True(Cv2.ImEncode(".jpg", image, out var jpeg));
        var frame = new CapturedFrame(sequence, DateTimeOffset.FromUnixTimeMilliseconds(capturedAt), image.Width, image.Height,
            jpeg, new FrameChangeDecision(0.1, true));
        var detection = new SamDetection(1, new PixelBox(8, 8, 40, 40), 0.9f, 8, 8, Enumerable.Repeat((byte)1, 64).ToArray());
        var result = new SamSegmentationResult("owned-semantic-fixture", frame, 10,
            Array.AsReadOnly(new[] { detection }));
        return new VerifiedMaskBatch(session, result, new Dictionary<int, SamDetection> { [42] = detection });
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

}
