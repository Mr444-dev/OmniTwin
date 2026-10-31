using System;
using System.Collections.Generic;
using System.Text.Json;
using OmniTwin.Contracts;
using static OmniTwin.Transport.WorldStreamCodec;

namespace OmniTwin.Transport;

/// <summary>Closed-schema, bounded JSON for measured telemetry, operator missions, and validated target replies.</summary>
public static class SwarmWireCodec
{
    /// <summary>Maximum UTF-8 body accepted by any swarm endpoint.</summary>
    public const int MaximumBytes = 65_536;
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16 };

    /// <summary>Encodes owned telemetry.</summary>
    public static byte[] EncodeTelemetry(SwarmTelemetrySnapshot value) => Encode(value);
    /// <summary>Encodes a fully validated control reply.</summary>
    public static byte[] EncodeControl(SwarmControlSnapshot value) => Encode(value);
    /// <summary>Encodes an explicit operator mission.</summary>
    public static byte[] EncodeMission(SwarmMissionRequest value) => Encode(value);
    private static byte[] Encode<T>(T value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > MaximumBytes) throw new WorldStreamProtocolException("swarm_body_size");
        return bytes;
    }
    private static T Decode<T>(ReadOnlyMemory<byte> bytes, Func<JsonElement, T> parse)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new WorldStreamProtocolException("swarm_body_size");
        try { using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }); return parse(document.RootElement); }
        catch (WorldStreamProtocolException) { throw; }
        catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidOperationException || error is OverflowException)
        { throw new WorldStreamProtocolException("invalid_swarm_payload"); }
    }
    /// <summary>Decodes unique, finite measured agent state before it can affect planning.</summary>
    public static SwarmTelemetrySnapshot DecodeTelemetry(ReadOnlyMemory<byte> bytes) => Decode(bytes, root =>
    {
        Keys(root, "schemaVersion", "sessionId", "sequenceNumber", "timestampUnixMs", "agents"); Version(root);
        var agents = new List<AgentTelemetry>();
        foreach (var agent in Array(root, "agents", 128))
        {
            Keys(agent, "agentId", "role", "currentPosition", "currentVelocity", "batteryLevel", "status");
            agents.Add(new AgentTelemetry(Int(agent, "agentId"), (AgentRole)Int(agent, "role"),
                Vector(agent.GetProperty("currentPosition")), Vector(agent.GetProperty("currentVelocity")),
                Scalar(agent, "batteryLevel"), (AgentStatus)Int(agent, "status")));
        }
        return new SwarmTelemetrySnapshot(Id(root, "sessionId"), Long(root, "sequenceNumber"), Long(root, "timestampUnixMs"), agents);
    });
    /// <summary>Decodes an operator request; unknown fields, enum values, and duplicate keys are rejected.</summary>
    public static SwarmMissionRequest DecodeMission(ReadOnlyMemory<byte> bytes) => Decode(bytes, root =>
    {
        Keys(root, "command", "useBrain"); var command = root.GetProperty("command");
        Keys(command, "commandId", "kind", "instruction", "issuedAtUnixMs", "targetObstacleId", "safetyRadiusMeters");
        var target = NullableLong(command, "targetObstacleId");
        if (target > int.MaxValue) throw new WorldStreamProtocolException("target_id_range");
        return new SwarmMissionRequest(new SwarmMissionCommand(Id(command, "commandId"), (SwarmMissionKind)Int(command, "kind"),
            Text(command, "instruction"), Long(command, "issuedAtUnixMs"), (int?)target, Scalar(command, "safetyRadiusMeters")), Boolean(root, "useBrain"));
    });
    /// <summary>Decodes reply correlation, provenance, diagnostics, bounds, and complete finite directives.</summary>
    public static SwarmControlSnapshot DecodeControl(ReadOnlyMemory<byte> bytes) => Decode(bytes, root =>
    {
        Keys(root, "schemaVersion", "telemetrySessionId", "telemetrySequenceNumber", "timestampUnixMs", "isSourceCurrent", "perceptionStreamId",
            "calibrationId", "worldSequenceNumber", "bounds", "policyName", "perceptionFailureCategory", "brainStatus", "brainDiagnostic",
            "modelIdUsed", "usedModelFallback", "generationAttempts", "compilationDiagnostics", "directives"); Version(root);
        var bounds = root.GetProperty("bounds"); Keys(bounds, "minimumX", "maximumX", "minimumZ", "maximumZ");
        var diagnostics = new List<string>();
        foreach (var item in Array(root, "compilationDiagnostics", 20))
        { if (item.ValueKind != JsonValueKind.String) throw new WorldStreamProtocolException("expected_diagnostic"); diagnostics.Add(item.GetString()!); }
        var directives = ReadDirectives(root.GetProperty("directives"));
        return new SwarmControlSnapshot(Id(root, "telemetrySessionId"), Long(root, "telemetrySequenceNumber"), Long(root, "timestampUnixMs"),
            Boolean(root, "isSourceCurrent"), NullableId(root, "perceptionStreamId"), NullableId(root, "calibrationId"), NullableLong(root, "worldSequenceNumber"),
            new WorkspaceBounds(Scalar(bounds, "minimumX"), Scalar(bounds, "maximumX"), Scalar(bounds, "minimumZ"), Scalar(bounds, "maximumZ")),
            Text(root, "policyName"), NullableText(root, "perceptionFailureCategory"), Text(root, "brainStatus"), NullableText(root, "brainDiagnostic"),
            NullableText(root, "modelIdUsed"), Boolean(root, "usedModelFallback"), Int(root, "generationAttempts"), diagnostics, directives);
    });

    /// <summary>Decodes worker output using the same strict vector, recipient, speed and collection boundary.</summary>
    public static IReadOnlyList<AgentTargetDirective> DecodeDirectives(ReadOnlyMemory<byte> bytes) => Decode(bytes, root => ReadDirectives(root));
    /// <summary>Encodes bounded worker directives.</summary>
    public static byte[] EncodeDirectives(IReadOnlyList<AgentTargetDirective> directives) => Encode(directives);
    private static IReadOnlyList<AgentTargetDirective> ReadDirectives(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 128) throw new WorldStreamProtocolException("directive_limit");
        var result = new List<AgentTargetDirective>(); var ids = new HashSet<int>();
        foreach (var directive in root.EnumerateArray())
        {
            Keys(directive, "agentId", "targetPosition", "desiredSpeed", "statusTag"); var id = Int(directive, "agentId");
            if (!ids.Add(id)) throw new WorldStreamProtocolException("duplicate_directive");
            result.Add(new AgentTargetDirective(id, Vector(directive.GetProperty("targetPosition")), Scalar(directive, "desiredSpeed"), Text(directive, "statusTag")));
        }
        return result.AsReadOnly();
    }
    private static JsonElement.ArrayEnumerator Array(JsonElement root, string key, int maximum)
    {
        var item = root.GetProperty(key);
        if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() > maximum) throw new WorldStreamProtocolException("swarm_collection_limit");
        return item.EnumerateArray();
    }
    private static Vector2D Vector(JsonElement value) { Keys(value, "x", "z"); return new Vector2D(Scalar(value, "x"), Scalar(value, "z")); }
    private static Guid? NullableId(JsonElement root, string key) => root.GetProperty(key).ValueKind == JsonValueKind.Null ? (Guid?)null : Id(root, key);
    private static void Version(JsonElement root) { if (Int(root, "schemaVersion") != 1) throw new WorldStreamProtocolException("unsupported_swarm_schema"); }
}
