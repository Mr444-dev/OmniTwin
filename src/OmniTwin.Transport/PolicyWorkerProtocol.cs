using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using OmniTwin.Contracts;
using static OmniTwin.Transport.WorldStreamCodec;

namespace OmniTwin.Transport;

/// <summary>Bounded length-prefixed IPC; a compromised worker cannot force an unbounded host allocation.</summary>
public static class PolicyWorkerProtocol
{
    /// <summary>Writes one frame, rejecting oversized payloads before any I/O.</summary>
    public static async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length == 0 || bytes.Length > SwarmWireCodec.MaximumBytes) throw new WorldStreamProtocolException("worker_frame_size");
        var size = bytes.Length; var header = new[] { (byte)size, (byte)(size >> 8), (byte)(size >> 16), (byte)(size >> 24) };
        await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    /// <summary>Reads exactly one frame with a strict 64 KiB allocation cap.</summary>
    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await Exact(stream, header, token).ConfigureAwait(false);
        var size = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
        if (size <= 0 || size > SwarmWireCodec.MaximumBytes) throw new WorldStreamProtocolException("worker_frame_size");
        var bytes = new byte[size]; await Exact(stream, bytes, token).ConfigureAwait(false); return bytes;
    }
    private static async Task Exact(Stream stream, byte[] bytes, CancellationToken token)
    {
        var offset = 0;
        while (offset < bytes.Length)
        { var count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false); if (count == 0) throw new EndOfStreamException(); offset += count; }
    }
    /// <summary>Encodes an owned finite world and deterministic simulation time.</summary>
    public static byte[] EncodeRequest(WorldStateSnapshot world, float seconds)
    {
        if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { world, seconds }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (bytes.Length > SwarmWireCodec.MaximumBytes) throw new WorldStreamProtocolException("worker_frame_size"); return bytes;
    }
    /// <summary>Validates every IPC input field, collection bound, enum and vector.</summary>
    public static (WorldStateSnapshot World, float Seconds) DecodeRequest(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > SwarmWireCodec.MaximumBytes) throw new WorldStreamProtocolException("worker_frame_size");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }); var root = document.RootElement;
            Keys(root, "world", "seconds"); var world = root.GetProperty("world");
            Keys(world, "schemaVersion", "sequenceNumber", "timestampUnixMs", "obstacles", "agents", "activePolicyName", "lastVoiceCommand");
            var obstacles = world.GetProperty("obstacles"); var agents = world.GetProperty("agents");
            if (obstacles.ValueKind != JsonValueKind.Array || obstacles.GetArrayLength() > 128 || agents.ValueKind != JsonValueKind.Array || agents.GetArrayLength() > 128)
                throw new WorldStreamProtocolException("worker_collection_limit");
            var found = new List<SegmentedObstacle>(); var measured = new List<AgentTelemetry>();
            foreach (var item in obstacles.EnumerateArray())
            {
                Keys(item, "id", "label", "worldPosition", "radiusMeters", "confidence", "isHazardZone");
                found.Add(new SegmentedObstacle(Int(item, "id"), Text(item, "label"), Vector(item.GetProperty("worldPosition")), Scalar(item, "radiusMeters"), Scalar(item, "confidence"), Boolean(item, "isHazardZone")));
            }
            foreach (var item in agents.EnumerateArray())
            {
                Keys(item, "agentId", "role", "currentPosition", "currentVelocity", "batteryLevel", "status");
                measured.Add(new AgentTelemetry(Int(item, "agentId"), (AgentRole)Int(item, "role"), Vector(item.GetProperty("currentPosition")), Vector(item.GetProperty("currentVelocity")), Scalar(item, "batteryLevel"), (AgentStatus)Int(item, "status")));
            }
            var seconds = Scalar(root, "seconds"); if (seconds < 0) throw new WorldStreamProtocolException("worker_time");
            return (new WorldStateSnapshot(Long(world, "timestampUnixMs"), found, measured, Text(world, "activePolicyName"), TextAllowEmpty(world, "lastVoiceCommand"), Long(world, "sequenceNumber"), Int(world, "schemaVersion")), seconds);
        }
        catch (WorldStreamProtocolException) { throw; }
        catch (Exception error) when (error is JsonException || error is ArgumentException || error is InvalidOperationException || error is OverflowException)
        { throw new WorldStreamProtocolException("invalid_worker_payload"); }
    }
    private static Vector2D Vector(JsonElement item) { Keys(item, "x", "z"); return new Vector2D(Scalar(item, "x"), Scalar(item, "z")); }
    private static string TextAllowEmpty(JsonElement item, string key)
    { var text = item.GetProperty(key); if (text.ValueKind != JsonValueKind.String) throw new WorldStreamProtocolException("worker_text"); return text.GetString()!; }
}
