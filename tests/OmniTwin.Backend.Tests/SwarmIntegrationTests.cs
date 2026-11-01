using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniTwin.Backend;
using OmniTwin.Backend.Services;
using OmniTwin.Contracts;
using OmniTwin.Transport;
using Xunit;

namespace OmniTwin.Backend.Tests;

public sealed class SwarmIntegrationTests
{
    private static SwarmTelemetrySnapshot Telemetry(Guid? session = null, long sequence = 1) => new(session ?? Guid.NewGuid(), sequence,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Enumerable.Range(1, 5).Select(id => new AgentTelemetry(id,
            id <= 3 ? AgentRole.GroundVehicle : AgentRole.AerialDrone, new Vector2D(id - 7, -3), default, 1, AgentStatus.Holding)).ToArray());

    [Fact]
    public async Task DisabledSourceHoldsEveryMeasuredAgentAndIdenticalRetriesAreIdempotent()
    {
        await using var server = await Server.StartAsync(); var telemetry = Telemetry(); var bytes = SwarmWireCodec.EncodeTelemetry(telemetry);
        using var first = await server.PostAsync("/api/swarm/telemetry", bytes);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var control = SwarmWireCodec.DecodeControl(await first.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.False(control.IsSourceCurrent); Assert.Equal(5, control.Directives.Count);
        Assert.All(control.Directives, item => Assert.Equal(0, item.DesiredSpeed));
        using var retry = await server.PostAsync("/api/swarm/telemetry", bytes); Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var conflict = new SwarmTelemetrySnapshot(telemetry.SessionId, telemetry.SequenceNumber, telemetry.TimestampUnixMs + 1, telemetry.Agents);
        using var rejected = await server.PostAsync("/api/swarm/telemetry", SwarmWireCodec.EncodeTelemetry(conflict));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
    }

    [Fact]
    public async Task ControlClientRecoversWhenThePreviousReplaySessionExpires()
    {
        await using var server = await Server.StartAsync();
        using var incumbent = await server.PostAsync("/api/swarm/telemetry", SwarmWireCodec.EncodeTelemetry(Telemetry()));
        Assert.Equal(HttpStatusCode.OK, incumbent.StatusCode);

        using var client = new SwarmControlClient();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(8));
        var challengeSession = Guid.NewGuid();
        long sequence = 1;
        client.PublishTelemetry(Telemetry(challengeSession));
        var running = client.RunAsync(server.Http.BaseAddress!, stop.Token);
        try
        {
            var sawControlledConflict = false;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6);
            while (DateTimeOffset.UtcNow < deadline && !client.GetView().HasFreshReply)
            {
                if (client.GetView().FailureCategory == "control_session_in_use") sawControlledConflict = true;
                client.PublishTelemetry(Telemetry(challengeSession, Interlocked.Increment(ref sequence)));
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            Assert.True(client.GetView().HasFreshReply, "The telemetry loop did not recover after the old session expired.");
            Assert.True(sawControlledConflict, "The initial 409 was not classified as a recoverable session conflict.");
            Assert.True(client.GetView().Control!.IsSourceCurrent is false);
            Assert.Equal(challengeSession, client.GetView().Control!.TelemetrySessionId);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    [Fact]
    public async Task RealControlClientReceivesExplicitAuthFailureWithoutFabricatedWorld()
    {
        await using var server = await Server.StartAsync();
        server.App.Services.GetRequiredService<BackendRuntimeState>().Set("faulted", "auth_missing_or_invalid_401");
        using var client = new SwarmControlClient(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(8));
        client.PublishTelemetry(Telemetry()); var running = client.RunAsync(server.Http.BaseAddress!, stop.Token);
        try
        {
            while (!client.GetView().HasFreshReply) await Task.Delay(20, stop.Token);
            var control = client.GetView().Control!;
            Assert.Equal("auth_missing_or_invalid_401", control.PerceptionFailureCategory); Assert.False(control.IsSourceCurrent);
            Assert.All(control.Directives, directive => Assert.Equal(0, directive.DesiredSpeed));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await server.Http.GetAsync("/api/world/latest", stop.Token)).StatusCode);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
        Assert.Null(client.GetView().Control);
    }

    [Fact]
    public async Task SwarmEndpointsRejectCrossOriginAndInvalidBodiesAndAcceptNativeMissions()
    {
        await using var server = await Server.StartAsync();
        using var invalid = await server.PostAsync("/api/swarm/telemetry", System.Text.Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/swarm/telemetry") { Content = Content(SwarmWireCodec.EncodeTelemetry(Telemetry())) };
        request.Headers.Add("Origin", "https://evil.example"); request.Headers.Add("X-OmniTwin-Swarm", "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Http.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        var mission = new SwarmMissionRequest(new SwarmMissionCommand(Guid.NewGuid(), SwarmMissionKind.HoldPosition, "Hold the real simulation agents",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, 3), false);
        using var accepted = await server.PostAsync("/api/swarm/mission", SwarmWireCodec.EncodeMission(mission));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var duplicate = await server.PostAsync("/api/swarm/mission", SwarmWireCodec.EncodeMission(mission));
        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task RealIsolatedExecutionTimeoutSelectsPatrolThenSourceLossSelectsEmergencyHold()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string source = """
            using System.Collections.Generic; using OmniTwin.Contracts;
            namespace OmniTwin.Generated;
            public sealed class EndlessPolicy : ISwarmPolicy {
                public string PolicyName => "Endless fixture";
                public IReadOnlyList<AgentTargetDirective> ComputeDirectives(WorldStateSnapshot worldState, float timeSeconds) {
                    while (timeSeconds >= 0) { } return new AgentTargetDirective[0];
                }
            }
            """;
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (!System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "OmniTwin.sln"))) root = root.Parent!;
        var configuration = new System.IO.DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var compiled = await new OmniTwin.Brain.RoslynPolicyCompiler().CompileAsync(source, TestContext.Current.CancellationToken);
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        var sandbox = new OmniTwin.Brain.WindowsPolicySandbox(System.IO.Path.Combine(root.FullName, "src/OmniTwin.PolicyWorker/bin", configuration, "net10.0"), System.IO.Path.Combine(root.FullName, "local/policy-sandbox-tests"));
        using var worker = await sandbox.StartAsync(compiled.Image!, TestContext.Current.CancellationToken);
        var options = new BackendOptions(); var broker = new OmniTwin.Backend.Streaming.SnapshotBroker(options, TimeProvider.System);
        var runtime = new BackendRuntimeState(broker, options); runtime.Set("running");
        using var coordinator = new OmniTwin.Backend.Swarm.SwarmCoordinator(broker, runtime, options, TimeProvider.System);
        var mission = new SwarmMissionCommand(Guid.NewGuid(), SwarmMissionKind.PatrolPerimeter, "Owned failure fixture", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, 3);
        coordinator.SelectMission(mission); Assert.True(coordinator.Activate(mission.CommandId, worker));
        broker.Publish(SnapshotBrokerTests.Snapshot(1), null, 0);
        var measured = Telemetry(); var control = await coordinator.EvaluateAsync(measured, TestContext.Current.CancellationToken);
        Assert.Equal("PerimeterPatrolPolicy", control.PolicyName); Assert.Equal("fallback", control.BrainStatus); Assert.Equal("execution_timeout", control.BrainDiagnostic);
        Assert.True(control.IsSourceCurrent); Assert.Equal(5, control.Directives.Count);
        runtime.Set("faulted", "camera_frame_timeout");
        var held = await coordinator.EvaluateAsync(new SwarmTelemetrySnapshot(measured.SessionId, 2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), measured.Agents), TestContext.Current.CancellationToken);
        Assert.Equal("EmergencyHoldPolicy", held.PolicyName); Assert.All(held.Directives, directive => Assert.Equal(0, directive.DesiredSpeed));
    }

    private static ByteArrayContent Content(byte[] bytes)
    { var content = new ByteArrayContent(bytes); content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); return content; }
    private sealed class Server : IAsyncDisposable
    {
        internal WebApplication App { get; }
        internal HttpClient Http { get; }
        private Server(WebApplication app, Uri address) { App = app; Http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) }; }
        internal static async Task<Server> StartAsync()
        {
            var app = BackendApplication.Create(Array.Empty<string>(), builder =>
            { builder.Configuration["OmniTwin:Port"] = "0"; builder.Configuration["OmniTwin:EnablePerception"] = "false"; builder.Logging.ClearProviders(); });
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Server(app, new Uri(address));
        }
        internal Task<HttpResponseMessage> PostAsync(string path, byte[] bytes)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Content(bytes) }; request.Headers.Add("X-OmniTwin-Swarm", "1");
            return Http.SendAsync(request, TestContext.Current.CancellationToken);
        }
        public async ValueTask DisposeAsync()
        { Http.Dispose(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await App.StopAsync(deadline.Token); await App.DisposeAsync(); }
    }
}
