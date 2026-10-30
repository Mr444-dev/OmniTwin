using System;
using System.Collections.Generic;
using System.Text.Json;
using OmniTwin.Contracts;

namespace OmniTwin.Transport;

/// <summary>A sanitized wire-format failure. Invalid payloads must not be passed to simulation.</summary>
public sealed class WorldStreamProtocolException : FormatException
{
    /// <summary>Creates a protocol failure without including untrusted payload text.</summary>
    public WorldStreamProtocolException(string category) : base("World stream protocol failure: " + category) => Category = category;
    /// <summary>Gets the stable failure category.</summary>
    public string Category { get; }
}

/// <summary>Encodes camel-case version-one messages and validates every field before constructing shared DTOs.</summary>
public static class WorldStreamCodec
{
    /// <summary>The required WebSocket subprotocol.</summary>
    public const string SubProtocol = "omnitwin.v1";
    /// <summary>The maximum UTF-8 size of one complete WebSocket message.</summary>
    public const int MaximumMessageBytes = 262_144;
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16 };

    /// <summary>Serializes a validated immutable message into independently owned UTF-8 bytes.</summary>
    public static byte[] Encode(WorldStreamMessage message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (bytes.Length > MaximumMessageBytes) throw new WorldStreamProtocolException("message_too_large");
        return bytes;
    }

    /// <summary>Rejects missing/unknown/duplicate keys, invalid versions, invalid scalars, inconsistent ages, and oversized collections.</summary>
    public static WorldStreamMessage Decode(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > MaximumMessageBytes) throw new WorldStreamProtocolException("message_size");
        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            Keys(root, "protocolVersion", "messageType", "streamId", "sequenceNumber", "sentAtUnixMs", "retentionMilliseconds",
                "world", "failureCategory", "rejectedDetectionCount");
            if (Int(root, "protocolVersion") != 1 || Text(root, "messageType") != "world.perception")
                throw new WorldStreamProtocolException("unsupported_protocol");
            return new WorldStreamMessage(Id(root, "streamId"), Long(root, "sequenceNumber"), Long(root, "sentAtUnixMs"),
                Long(root, "retentionMilliseconds"), World(root.GetProperty("world")), NullableText(root, "failureCategory"),
                Int(root, "rejectedDetectionCount"));
        }
        catch (WorldStreamProtocolException) { throw; }
        catch (JsonException) { throw new WorldStreamProtocolException("invalid_json"); }
        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is OverflowException)
        { throw new WorldStreamProtocolException("invalid_value"); }
    }

    private static ObstacleTrackingSnapshot World(JsonElement root)
    {
        Keys(root, "schemaVersion", "sessionId", "calibrationId", "sequenceNumber", "sourceSequenceNumber", "sourceAgeMilliseconds",
            "isObservationCurrent", "timestampUnixMs", "obstacles");
        var collection = root.GetProperty("obstacles");
        if (collection.ValueKind != JsonValueKind.Array || collection.GetArrayLength() > 128)
            throw new WorldStreamProtocolException("track_limit");
        var tracks = new List<TrackedObstacleState>(collection.GetArrayLength());
        foreach (var track in collection.EnumerateArray())
        {
            Keys(track, "obstacle", "lastObservedUnixMs", "ageMilliseconds", "trackingConfidence", "seenInLatestObservation");
            var obstacle = track.GetProperty("obstacle");
            Keys(obstacle, "id", "label", "worldPosition", "radiusMeters", "confidence", "isHazardZone");
            var position = obstacle.GetProperty("worldPosition");
            Keys(position, "x", "z");
            var value = new SegmentedObstacle(Int(obstacle, "id"), Text(obstacle, "label"),
                new Vector2D(Scalar(position, "x"), Scalar(position, "z")), Scalar(obstacle, "radiusMeters"),
                Scalar(obstacle, "confidence"), Boolean(obstacle, "isHazardZone"));
            tracks.Add(new TrackedObstacleState(value, Long(track, "lastObservedUnixMs"), Long(track, "ageMilliseconds"),
                Scalar(track, "trackingConfidence"), Boolean(track, "seenInLatestObservation")));
        }
        return new ObstacleTrackingSnapshot(Id(root, "sessionId"), Id(root, "calibrationId"), Long(root, "sequenceNumber"),
            NullableLong(root, "sourceSequenceNumber"), NullableLong(root, "sourceAgeMilliseconds"), Boolean(root, "isObservationCurrent"),
            Long(root, "timestampUnixMs"), tracks, Int(root, "schemaVersion"));
    }

    private static void Keys(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new WorldStreamProtocolException("expected_object");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (Array.IndexOf(expected, property.Name) < 0 || !names.Add(property.Name))
                throw new WorldStreamProtocolException("unknown_or_duplicate_field");
        if (names.Count != expected.Length) throw new WorldStreamProtocolException("missing_field");
    }
    private static string Text(JsonElement value, string key)
    {
        var item = value.GetProperty(key);
        if (item.ValueKind != JsonValueKind.String) throw new WorldStreamProtocolException("expected_string");
        return item.GetString()!;
    }
    private static string? NullableText(JsonElement value, string key) => value.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Text(value, key);
    private static Guid Id(JsonElement value, string key)
    {
        if (!Guid.TryParseExact(Text(value, key), "D", out var id) || id == Guid.Empty) throw new WorldStreamProtocolException("invalid_id");
        return id;
    }
    private static long Long(JsonElement value, string key)
    {
        var item = value.GetProperty(key);
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var number) || number < 0)
            throw new WorldStreamProtocolException("invalid_integer");
        return number;
    }
    private static long? NullableLong(JsonElement value, string key) => value.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Long(value, key);
    private static int Int(JsonElement value, string key)
    {
        var number = Long(value, key);
        if (number > int.MaxValue) throw new WorldStreamProtocolException("integer_range");
        return (int)number;
    }
    private static bool Boolean(JsonElement value, string key)
    {
        var item = value.GetProperty(key);
        if (item.ValueKind != JsonValueKind.True && item.ValueKind != JsonValueKind.False)
            throw new WorldStreamProtocolException("expected_boolean");
        return item.GetBoolean();
    }
    private static float Scalar(JsonElement value, string key)
    {
        var item = value.GetProperty(key);
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out var number) || float.IsNaN(number) || float.IsInfinity(number))
            throw new WorldStreamProtocolException("nonfinite_or_invalid_number");
        return number;
    }
}
