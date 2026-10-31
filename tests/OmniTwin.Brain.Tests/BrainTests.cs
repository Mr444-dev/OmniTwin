using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniTwin.Brain;
using OmniTwin.Contracts;
using OmniTwin.Policies;
using OmniTwin.Transport;
using Xunit;

namespace OmniTwin.Brain.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class BrainTests
{
    private const string Good = """
        using System;
        using System.Collections.Generic;
        using OmniTwin.Contracts;
        namespace OmniTwin.Generated;
        public sealed class VerifiedPolicy : ISwarmPolicy
        {
            public string PolicyName => "Verified hold";
            public IReadOnlyList<AgentTargetDirective> ComputeDirectives(WorldStateSnapshot worldState, float timeSeconds)
            {
                var result = new List<AgentTargetDirective>();
                foreach (var agent in worldState.Agents)
                    result.Add(new AgentTargetDirective(agent.AgentId, agent.CurrentPosition, MathF.Max(0, 0), "hold"));
                return result;
            }
        }
        """;
    private static WorldStateSnapshot World() => new(1, Array.Empty<SegmentedObstacle>(), new[] { new AgentTelemetry(1, AgentRole.GroundVehicle, default, default, 1, AgentStatus.Holding) }, "baseline", "hold");
    private static SwarmMissionCommand Mission() => new(Guid.NewGuid(), SwarmMissionKind.PatrolPerimeter, "Hold while assessing workspace", 1, null, 3);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static (string Worker, string Scratch) Paths()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (!File.Exists(Path.Combine(root.FullName, "OmniTwin.sln"))) root = root.Parent ?? throw new InvalidOperationException();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return (Path.Combine(root.FullName, "src", "OmniTwin.PolicyWorker", "bin", configuration, "net10.0"), Path.Combine(root.FullName, "local", "policy-sandbox-tests"));
    }
    [Fact]
    public async Task SemanticCompilerAcceptsCompletePolicyWithoutLoadingGeneratedAssembly()
    {
        var result = await new RoslynPolicyCompiler().CompileAsync(Good, Token);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics)); Assert.Empty(result.Diagnostics);
        Assert.Equal("OmniTwin.Generated.VerifiedPolicy", result.Image!.TypeName);
        var bytes = result.Image.CopyImage(); bytes[0] = 0; Assert.NotEqual(0, result.Image.CopyImage()[0]);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name!.StartsWith("OmniTwin.Generated.", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("global::System.Environment.Exit(0);")]
    [InlineData("var t = worldState.GetType();")]
    [InlineData("dynamic value = worldState; value.Compute();")]
    [InlineData("var t = typeof(WorldStateSnapshot);")]
    [InlineData("var t = System.Activator.CreateInstance<WorldStateSnapshot>();")]
    [InlineData("System.GC.Collect();")]
    [InlineData("System.Threading.Thread.Sleep(1);")]
    [InlineData("System.IO.File.ReadAllText(\"secret\");")]
    public async Task SemanticCompilerRejectsCapabilitiesIncludingFullyQualifiedBypasses(string expression)
    {
        var source = Good.Replace("var result =", expression + " var result =", StringComparison.Ordinal);
        var result = await new RoslynPolicyCompiler().CompileAsync(source, Token); Assert.False(result.Success); Assert.NotEmpty(result.Diagnostics);
    }
    [Fact]
    public async Task LinqUsesVerifiedConstructedSymbolsAndSourceComplexityIsBounded()
    {
        var compiler = new RoslynPolicyCompiler();
        var linq = Good.Replace("using System;", "using System; using System.Linq;", StringComparison.Ordinal)
            .Replace("in worldState.Agents)", "in worldState.Agents.OrderBy(agent => agent.AgentId).ToArray())", StringComparison.Ordinal);
        var accepted = await compiler.CompileAsync(linq, Token); Assert.True(accepted.Success, string.Join("\n", accepted.Diagnostics));
        Assert.False((await compiler.CompileAsync(new string('x', 65537), Token)).Success);
        var nested = Good.Replace("MathF.Max(0, 0)", new string('(', 300) + "0" + new string(')', 300), StringComparison.Ordinal);
        Assert.False((await compiler.CompileAsync(nested, Token)).Success);
    }
    [Fact]
    public async Task MalformedEnvironmentKeyIsAnExplicitAuthenticationFailureWithoutHeaderInjection()
    {
        await using var server = await Provider.StartAsync((_, _) => (200, Reply(Good)));
        using var client = new MuseSparkOpenClient(endpoint: server.Endpoint, apiKey: "owned\r\ninvalid-key");
        Assert.Equal("auth_missing_or_invalid_401", (await Assert.ThrowsAsync<BrainApiException>(() => client.GenerateAsync(World(), Mission(), Array.Empty<string>(), Token))).Category);
        Assert.Empty(server.Models);
    }
    [Fact]
    public async Task StrictWorkerProtocolRejectsUnknownKeysAndUnboundedHeaderBeforeAllocation()
    {
        var bytes = PolicyWorkerProtocol.EncodeRequest(World(), 1); var decoded = PolicyWorkerProtocol.DecodeRequest(bytes); Assert.Single(decoded.World.Agents);
        Assert.Throws<WorldStreamProtocolException>(() => PolicyWorkerProtocol.DecodeRequest(Encoding.UTF8.GetBytes("{\"world\":{},\"seconds\":1,\"extra\":0}")));
        using var stream = new MemoryStream(new byte[] { 0xff, 0xff, 0xff, 0x7f });
        await Assert.ThrowsAsync<WorldStreamProtocolException>(() => PolicyWorkerProtocol.ReadAsync(stream, Token));
    }
    [Fact]
    public async Task RealAppContainerDeniesOutsideFileAndLoopbackAndInheritsNoCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = Paths(); Directory.CreateDirectory(paths.Scratch); var canary = Path.Combine(paths.Scratch, "outside-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(canary, "owned isolation canary", Token);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            using (var host = new TcpClient())
            { await host.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Token); using var accepted = await listener.AcceptTcpClientAsync(Token); }
            var proof = await new WindowsPolicySandbox(paths.Worker, paths.Scratch).ProbeAsync(canary, ((IPEndPoint)listener.LocalEndpoint).Port, Token);
            Assert.Equal("container=True;fileDenied=True;networkDenied=True;noKeys=True", proof);
            Assert.False(listener.Pending());
        }
        finally { File.Delete(canary); }
    }
    [Fact]
    public async Task VerifiedPolicyRunsOnlyInIsolatedWorkerAndReturnsValidatedDirectives()
    {
        if (!OperatingSystem.IsWindows()) return;
        var image = await new RoslynPolicyCompiler().CompileAsync(Good, Token); Assert.True(image.Success, string.Join("\n", image.Diagnostics));
        var paths = Paths(); using var policy = await new WindowsPolicySandbox(paths.Worker, paths.Scratch).StartAsync(image.Image!, Token);
        Assert.Equal("Verified hold", policy.PolicyName);
        var targets = await policy.ComputeAsync(World(), 1, Token); Assert.Single(targets); Assert.Equal(0, targets[0].DesiredSpeed);
        Assert.Single(SafeNavigation.ValidateDirectives(World(), targets, WorkspaceBounds.TacticalDesk));
    }
    [Fact]
    public async Task InfiniteGeneratedComputeIsHardKilledWithinWallDeadline()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Good.Replace("var result =", "while (timeSeconds >= 0) { } var result =", StringComparison.Ordinal);
        var image = await new RoslynPolicyCompiler().CompileAsync(source, Token); Assert.True(image.Success, string.Join("\n", image.Diagnostics));
        var paths = Paths(); using var policy = await new WindowsPolicySandbox(paths.Worker, paths.Scratch).StartAsync(image.Image!, Token);
        var timer = Stopwatch.StartNew(); var error = await Assert.ThrowsAsync<PolicyExecutionException>(() => policy.ComputeAsync(World(), 1, Token));
        Assert.Equal("execution_timeout", error.Category); Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task ConstructorExceptionsStayInsideWorker()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Good.Replace("public string PolicyName", "public VerifiedPolicy() { throw new InvalidOperationException(\"untrusted body\"); } public string PolicyName", StringComparison.Ordinal);
        var image = await new RoslynPolicyCompiler().CompileAsync(source, Token); Assert.True(image.Success, string.Join("\n", image.Diagnostics));
        var paths = Paths(); var error = await Assert.ThrowsAsync<PolicyExecutionException>(() => new WindowsPolicySandbox(paths.Worker, paths.Scratch).StartAsync(image.Image!, Token));
        Assert.Equal("worker_protocol_or_exit", error.Category); Assert.DoesNotContain("untrusted", error.Message, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("var doomed = new byte[512 * 1024 * 1024]; if (doomed.Length > 0) throw new InvalidOperationException();")]
    [InlineData("var invalid = new Vector2D(float.NaN, 0);")]
    [InlineData("var invalid = new Vector2D(float.PositiveInfinity, 0);")]
    public async Task MemoryPressureAndNonfiniteVectorsCannotEscapeIntoHost(string expression)
    {
        if (!OperatingSystem.IsWindows()) return;
        var image = await new RoslynPolicyCompiler().CompileAsync(Good.Replace("var result =", expression + " var result =", StringComparison.Ordinal), Token);
        Assert.True(image.Success, string.Join("\n", image.Diagnostics)); var paths = Paths();
        using var policy = await new WindowsPolicySandbox(paths.Worker, paths.Scratch).StartAsync(image.Image!, Token);
        await Assert.ThrowsAsync<PolicyExecutionException>(() => policy.ComputeAsync(World(), 1, Token));
        Assert.Single(World().Agents);
    }
    [Fact]
    public void StrictDirectiveValidationClampsFiniteTargetsAndRejectsWrongRecipientsAndSpeed()
    {
        var far = new[] { new AgentTargetDirective(1, new Vector2D(1000, -1000), 1, "move") };
        var result = SafeNavigation.ValidateDirectives(World(), far, WorkspaceBounds.TacticalDesk);
        Assert.True(WorkspaceBounds.TacticalDesk.Contains(result[0].TargetPosition, SafeNavigation.ClearanceMeters));
        Assert.Throws<ArgumentException>(() => SafeNavigation.ValidateDirectives(World(), new[] { new AgentTargetDirective(2, default, 1, "wrong") }, WorkspaceBounds.TacticalDesk));
        Assert.Throws<ArgumentException>(() => SafeNavigation.ValidateDirectives(World(), new[] { new AgentTargetDirective(1, default, 9, "fast") }, WorkspaceBounds.TacticalDesk));
    }
    [Fact]
    public async Task Dated404FallsBackOnceAndReportsTheActualAlias()
    {
        await using var server = await Provider.StartAsync((attempt, _) => attempt == 1 ? (404, "discard private upstream body") : (200, Reply(Good)));
        using var client = new MuseSparkOpenClient(endpoint: server.Endpoint, apiKey: "owned-test-credential");
        var first = await client.GenerateAsync(World(), Mission(), Array.Empty<string>(), Token);
        var second = await client.GenerateAsync(World(), Mission(), Array.Empty<string>(), Token);
        Assert.True(first.UsedModelFallback); Assert.Equal(MuseSparkOpenClient.AliasModel, second.ModelIdUsed);
        Assert.Equal(new[] { MuseSparkOpenClient.RequiredModel, MuseSparkOpenClient.AliasModel, MuseSparkOpenClient.AliasModel }, server.Models);
    }
    [Theory]
    [InlineData(401, "auth_missing_or_invalid_401", 1)]
    [InlineData(403, "permission_denied_403", 1)]
    [InlineData(429, "rate_limited_429", 3)]
    [InlineData(504, "upstream_timeout", 3)]
    public async Task AuthenticationPermissionAndRateFailuresNeverSubstituteModels(int status, string category, int calls)
    {
        await using var server = await Provider.StartAsync((_, _) => (status, "private-provider-body"));
        using var client = new MuseSparkOpenClient(endpoint: server.Endpoint, apiKey: "owned-test-credential");
        var error = await Assert.ThrowsAsync<BrainApiException>(() => client.GenerateAsync(World(), Mission(), Array.Empty<string>(), Token));
        Assert.Equal(category, error.Category); Assert.False(error.UsedModelFallback); Assert.Equal(calls, server.Models.Count);
        Assert.All(server.Models, model => Assert.Equal(MuseSparkOpenClient.RequiredModel, model)); Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task SelfHealingUsesCompilerFeedbackAndActivatesRealIsolatedSecondCandidate()
    {
        if (!OperatingSystem.IsWindows()) return;
        var broken = Good.Replace("return result;", "return result", StringComparison.Ordinal);
        await using var server = await Provider.StartAsync((attempt, _) => (200, Reply(attempt == 1 ? broken : Good)));
        using var client = new MuseSparkOpenClient(endpoint: server.Endpoint, apiKey: "owned-test-credential"); var paths = Paths(); var progress = new List<BrainProgress>();
        var policy = await new SelfHealingLoop(client, new RoslynPolicyCompiler(), paths.Worker, paths.Scratch).RunAsync(World(), Mission(), progress.Add, Token);
        using var owned = policy as IDisposable; Assert.True(policy is not null, string.Join(" | ", progress.Select(item => item.ToString()))); Assert.InRange(server.Models.Count, 2, 3);
        Assert.Equal(server.Models.Count, progress[^1].Attempts);
        Assert.Contains("CS1002", server.Bodies[1], StringComparison.Ordinal); Assert.Contains(progress, item => item.Phase == "repairing"); Assert.Equal("verified", progress[^1].Phase);
        using var request = JsonDocument.Parse(server.Bodies[1]);
        using var repairContext = JsonDocument.Parse(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert.Equal(broken, repairContext.RootElement.GetProperty("previousSource").GetString());
    }
    [Fact]
    public async Task SelfHealingStopsAfterThreeRejectedCandidatesAndMissingKeyMakesNoHttpCall()
    {
        await using var server = await Provider.StartAsync((_, _) => (200, Reply("public broken syntax")));
        using var client = new MuseSparkOpenClient(endpoint: server.Endpoint, apiKey: "owned-test-credential"); var paths = Paths(); var progress = new List<BrainProgress>();
        Assert.Null(await new SelfHealingLoop(client, new RoslynPolicyCompiler(), paths.Worker, paths.Scratch).RunAsync(World(), Mission(), progress.Add, Token));
        Assert.Equal(3, server.Models.Count); Assert.Equal("generation_attempts_exhausted", progress[^1].Category);
        using var missing = new MuseSparkOpenClient(endpoint: server.Endpoint, apiKey: "");
        Assert.Equal("auth_missing_or_invalid_401", (await Assert.ThrowsAsync<BrainApiException>(() => missing.GenerateAsync(World(), Mission(), Array.Empty<string>(), Token))).Category);
        Assert.Equal(3, server.Models.Count);
    }
    [Fact]
    public async Task ContentSchemaRejectsUnknownFieldsAndNonJsonInsteadOfCompilingThem()
    {
        await using var server = await Provider.StartAsync((_, _) => (200, JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = "{\"policySource\":\"code\",\"decisionSummary\":\"ok\",\"extra\":true}" } } } })));
        using var client = new MuseSparkOpenClient(endpoint: server.Endpoint, apiKey: "owned-test-credential");
        Assert.Equal("invalid_response_schema", (await Assert.ThrowsAsync<BrainApiException>(() => client.GenerateAsync(World(), Mission(), Array.Empty<string>(), Token))).Category);
    }
    private static string Reply(string code) => JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = JsonSerializer.Serialize(new { policySource = code, decisionSummary = "Maintain safe measured positions." }) } } } });
    private sealed class Provider : IAsyncDisposable
    {
        internal readonly List<string> Models = new(); internal readonly List<string> Bodies = new(); private readonly WebApplication _app;
        internal Uri Endpoint { get; private set; } = null!;
        private Provider(WebApplication app) => _app = app;
        internal static async Task<Provider> StartAsync(Func<int, string, (int Status, string Body)> response)
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build(); var provider = new Provider(app);
            app.MapPost("/v1/chat/completions", async context =>
            {
                using var reader = new StreamReader(context.Request.Body); var body = await reader.ReadToEndAsync(context.RequestAborted);
                using var json = JsonDocument.Parse(body); provider.Bodies.Add(body); provider.Models.Add(json.RootElement.GetProperty("model").GetString()!);
                var answer = response(provider.Models.Count, body); context.Response.StatusCode = answer.Status; context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(answer.Body, context.RequestAborted);
            });
            await app.StartAsync(Token); var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            provider.Endpoint = new Uri(address + "/v1/chat/completions"); return provider;
        }
        public async ValueTask DisposeAsync() { await _app.StopAsync(Token); await _app.DisposeAsync(); }
    }
}
