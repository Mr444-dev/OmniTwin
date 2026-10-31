using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Transport;

namespace OmniTwin.Brain;

/// <summary>A safe provider failure; no credential, raw response or generated exception text is retained.</summary>
public sealed class BrainApiException : Exception
{
    internal BrainApiException(string category, string model, bool fallback) : base(category) { Category = category; ModelIdUsed = model; UsedModelFallback = fallback; }
    /// <summary>Gets a bounded operator-facing failure category.</summary>
    public string Category { get; }
    /// <summary>Gets the exact attempted provider model.</summary>
    public string ModelIdUsed { get; }
    /// <summary>Gets whether a dated-ID 404 authorized an alias retry.</summary>
    public bool UsedModelFallback { get; }
}

/// <summary>Validated generated source and concise public explanation, without private reasoning traces.</summary>
public sealed record PolicyGeneration(string PolicySource, string DecisionSummary, string ModelIdUsed, bool UsedModelFallback);

/// <summary>Bounded OpenAI-compatible Baseten adapter with exactly one model substitution condition: dated-ID HTTP 404.</summary>
public sealed class MuseSparkOpenClient : IDisposable
{
    /// <summary>The required initial model identifier.</summary>
    public const string RequiredModel = "meta-models/muse-spark-open-1026";
    /// <summary>The user-authorized official alias, used only after a dated-ID 404.</summary>
    public const string AliasModel = "meta-models/muse-spark-open";
    private readonly HttpClient _http; private readonly string? _key; private readonly Uri _endpoint; private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _gate = new(1, 1); private string _model = RequiredModel;
    /// <summary>Constructs the real adapter. Explicit clients/endpoints support owned local HTTP protocol fixtures.</summary>
    public MuseSparkOpenClient(HttpClient? http = null, Uri? endpoint = null, string? apiKey = null)
    {
        _ownsHttp = http is null; _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _endpoint = endpoint ?? new Uri("https://inference.baseten.co/v1/chat/completions");
        if (!(_endpoint.Scheme == "https" && _endpoint.Host == "inference.baseten.co") && !(_endpoint.Scheme == "http" && IPAddress.TryParse(_endpoint.Host, out var ip) && IPAddress.IsLoopback(ip)))
            throw new ArgumentException("Only Baseten HTTPS or an owned loopback protocol fixture is supported.", nameof(endpoint));
        _key = apiKey ?? Environment.GetEnvironmentVariable("BASETEN_API_KEY");
    }
    /// <summary>Generates one candidate; transient retries share a 45-second deadline and never run concurrently.</summary>
    public async Task<PolicyGeneration> GenerateAsync(WorldStateSnapshot world, SwarmMissionCommand mission, IReadOnlyList<string> compilerFeedback, CancellationToken token, WorkspaceBounds? bounds = null, string? previousSource = null)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(_key) || _key.Length > 4096 || _key.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
                throw Failure("auth_missing_or_invalid_401");
            if (world.Agents.Count > 128 || world.Obstacles.Count > 128) throw Failure("context_size_limit");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var failures = 0;
            while (true)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); attempt.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (previousSource?.Length > 65536) throw Failure("context_size_limit");
                    var context = JsonSerializer.Serialize(new { world, mission, bounds = bounds ?? WorkspaceBounds.TacticalDesk, previousSource, compilerFeedback = compilerFeedback.Take(20).Select(line => line.Length <= 512 ? line : line.Substring(0, 512)) });
                    if (context.Length > 60_000) throw Failure("context_size_limit");
                    var body = JsonSerializer.SerializeToUtf8Bytes(new { model = _model, stream = false, max_tokens = 8192, messages = new[] {
                        new { role = "system", content = SystemPrompt }, new { role = "user", content = context } } });
                    using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new ByteArrayContent(body) };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotFound && _model == RequiredModel) { _model = AliasModel; continue; }
                    if (!response.IsSuccessStatusCode)
                    {
                        var category = (int)response.StatusCode switch { 401 => "auth_missing_or_invalid_401", 403 => "permission_denied_403", 429 => "rate_limited_429", 408 or 504 => "upstream_timeout", 404 => "model_not_found_404", _ => "upstream_error" };
                        var retry = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500;
                        if (!retry || ++failures >= 3) throw Failure(category);
                        var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(250 * (1 << (failures - 1))));
                        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                        if (wait > TimeSpan.FromSeconds(10)) throw Failure(category);
                        await Task.Delay(wait, deadline.Token).ConfigureAwait(false); continue;
                    }
                    await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
                    using var memory = new MemoryStream(); var buffer = new byte[8192];
                    while (true) { var count = await stream.ReadAsync(buffer, attempt.Token).ConfigureAwait(false); if (count == 0) break; if (memory.Length + count > 262144) throw Failure("response_size_limit"); await memory.WriteAsync(buffer.AsMemory(0, count), attempt.Token).ConfigureAwait(false); }
                    return Parse(memory.ToArray());
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    if (deadline.IsCancellationRequested || ++failures >= 3) throw Failure("upstream_timeout");
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << (failures - 1))), deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or IOException)
                {
                    if (++failures >= 3) throw Failure("network_error");
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << (failures - 1))), deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw Failure("upstream_timeout"); }
        finally { _gate.Release(); }
    }
    private PolicyGeneration Parse(byte[] bytes)
    {
        try
        {
            using var outer = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 }); Unique(outer.RootElement);
            var choices = outer.RootElement.GetProperty("choices"); if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1) throw Failure("invalid_response_schema");
            var choice = choices[0]; if (choice.GetProperty("finish_reason").GetString() != "stop") throw Failure("generation_incomplete");
            var message = choice.GetProperty("message"); if (message.GetProperty("role").GetString() != "assistant") throw Failure("invalid_response_schema");
            var content = message.GetProperty("content").GetString(); if (content is null || content.Length > 70_000) throw Failure("response_size_limit");
            using var result = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 4 }); Unique(result.RootElement);
            var properties = result.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (properties.Length != 2 || !properties.Contains("policySource") || !properties.Contains("decisionSummary")) throw Failure("invalid_response_schema");
            var source = result.RootElement.GetProperty("policySource").GetString(); var summary = result.RootElement.GetProperty("decisionSummary").GetString();
            if (string.IsNullOrWhiteSpace(source) || source.Length > 65536 || string.IsNullOrWhiteSpace(summary) || summary.Length > 512 || summary.Any(char.IsControl)) throw Failure("invalid_response_schema");
            return new PolicyGeneration(source, summary, _model, _model == AliasModel);
        }
        catch (BrainApiException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { throw Failure("invalid_response_schema"); }
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new JsonException(); Unique(property.Value); } }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
    private BrainApiException Failure(string category) => new(category, _model, _model == AliasModel);
    /// <summary>Releases owned HTTP resources after synthesis has stopped.</summary>
    public void Dispose() { if (_ownsHttp) _http.Dispose(); _gate.Dispose(); }
    private const string SystemPrompt = """
        Generate one complete deterministic C# swarm policy. Return ONLY one JSON object with exactly string keys policySource and decisionSummary.
        decisionSummary is a concise public explanation, at most 512 characters. Do not return private reasoning or markdown.
        Use namespace OmniTwin.Generated and exactly one public sealed class implementing OmniTwin.Contracts.ISwarmPolicy, with a public parameterless constructor.
        Interface: string PolicyName {get;} IReadOnlyList<AgentTargetDirective> ComputeDirectives(WorldStateSnapshot worldState, float timeSeconds).
        DTO constructors: Vector2D(float x,float z); AgentTargetDirective(int agentId,Vector2D targetPosition,float desiredSpeed,string statusTag).
        World properties: Obstacles, Agents, SequenceNumber. Obstacle properties: Id, WorldPosition(X,Z), RadiusMeters, IsHazardZone.
        Agent properties: AgentId, Role (AgentRole.GroundVehicle or AerialDrone), CurrentPosition(X,Z), BatteryLevel.
        Return exactly one directive per actual agent, speed in [0,4], finite coordinates within the supplied bounds. Avoid obstacles with at least 0.55m clearance.
        Only primitive System types, Math, MathF, Array, System.Collections.Generic collections, System.Linq.Enumerable, and these Contracts are permitted.
        No I/O, reflection, GetType, typeof, dynamic, unsafe, attributes, process/thread APIs, randomness or wall-clock access. Never depend on external assemblies.
        Bounded loops only; every compute must finish below 100ms. Use measured world context. If no safe movement exists, return each agent's current position at speed zero.
        Compiler feedback gives errors from the previous candidate. Repair the whole class, preserving the requested mission.
        """;
}
