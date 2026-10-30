using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OmniTwin.Perception.Vision;

internal static class SamResponseParser
{
    internal static (string Id, IReadOnlyList<SamDetection> Detections) Parse(byte[] json, int width, int height)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        RejectDuplicateKeys(root);
        if (Text(root, "object") != "response" || Text(root, "status") != "completed" || Text(root, "model") != "sam-3.1")
            throw new FormatException("Expected a completed sam-3.1 response.");
        var id = Text(root, "id");
        if (id.Length == 0 || id.Length > 256) throw new FormatException("Invalid response ID.");
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new FormatException("Response contains a provider error.");
        var output = Required(root, "output", JsonValueKind.Array);
        var text = new StringBuilder();
        foreach (var message in output.EnumerateArray())
        {
            if (Text(message, "type") != "message" || Text(message, "role") != "assistant")
                throw new FormatException("Unexpected segmentation output item.");
            if (message.TryGetProperty("status", out var status) && status.GetString() != "completed")
                throw new FormatException("Incomplete output message.");
            foreach (var content in Required(message, "content", JsonValueKind.Array).EnumerateArray())
            {
                if (Text(content, "type") != "output_text") throw new FormatException("Expected segmentation text, not a refusal or tool call.");
                text.Append(Text(content, "text"));
            }
        }
        return (id, ParseGeometry(text.ToString().Trim(), width, height));
    }

    private static IReadOnlyList<SamDetection> ParseGeometry(string text, int width, int height)
    {
        var result = new List<SamDetection>();
        if (text.Length == 0) return result.AsReadOnly();
        var position = 0;
        Expect(text, ref position, "<0f>");
        var ids = new HashSet<int>();
        var pixelBudget = 4_194_304L;
        while (position < text.Length)
        {
            if (result.Count >= 128) throw new FormatException("Too many segmentation records.");
            var start = position;
            while (position < text.Length && text[position] >= '0' && text[position] <= '9') position++;
            var id = Integer(text.Substring(start, position - start));
            if (!ids.Add(id)) throw new FormatException("Duplicate object ID.");
            var box = Fields(ReadToken(text, ref position, "<|box;"), "x1", "y1", "x2", "y2", "w", "h", "c");
            var left = Integer(box["x1"]); var top = Integer(box["y1"]);
            var right = Integer(box["x2"]); var bottom = Integer(box["y2"]);
            if (Integer(box["w"]) != width || Integer(box["h"]) != height ||
                right < left || bottom < top || right >= width || bottom >= height)
                throw new FormatException("Box lies outside the submitted source image or dimensions disagree.");
            var confidence = Confidence(box["c"]);
            var mask = Fields(ReadToken(text, ref position, "<|mask;"), "x", "y", "c", "data");
            if (mask["x"] != "0" || mask["y"] != "0" || Confidence(mask["c"]) != confidence)
                throw new FormatException("Invalid mask origin or inconsistent confidence.");
            var data = mask["data"].Split(',');
            if (data.Length != 3) throw new FormatException("Invalid mask data fields.");
            var maskHeight = Integer(data[0]); var maskWidth = Integer(data[1]);
            var area = (long)maskHeight * maskWidth;
            pixelBudget -= area;
            if (maskWidth > width || maskHeight > height || area <= 0 || pixelBudget < 0)
                throw new FormatException("Mask dimensions exceed the image or decoding budget.");
            var raster = SamOneBitMaskCodec.Decode(data[2], maskWidth, maskHeight);
            result.Add(new SamDetection(id, new PixelBox(left, top, right, bottom), confidence, maskWidth, maskHeight, raster));
            if (position == text.Length) break;
            Expect(text, ref position, ",");
            if (position == text.Length) throw new FormatException("Trailing record separator.");
        }
        return result.AsReadOnly();
    }

    private static Dictionary<string, string> Fields(string token, params string[] required)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in token.Split(';'))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0) throw new FormatException("Malformed token field.");
            var key = field.Substring(0, separator);
            if (Array.IndexOf(required, key) < 0 || !result.TryAdd(key, field.Substring(separator + 1)))
                throw new FormatException("Unknown or duplicate token field.");
        }
        if (result.Count != required.Length) throw new FormatException("Missing token field, including requested confidence.");
        return result;
    }

    private static string ReadToken(string text, ref int position, string prefix)
    {
        Expect(text, ref position, prefix);
        var end = text.IndexOf("|>", position, StringComparison.Ordinal);
        if (end < 0) throw new FormatException("Unterminated geometry token.");
        var value = text.Substring(position, end - position);
        position = end + 2;
        return value;
    }

    private static void Expect(string text, ref int position, string expected)
    {
        if (position + expected.Length > text.Length ||
            string.CompareOrdinal(text, position, expected, 0, expected.Length) != 0)
            throw new FormatException("Invalid SAM image geometry grammar.");
        position += expected.Length;
    }

    private static int Integer(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new FormatException("Expected a nonnegative 32-bit integer.");
        return value;
    }

    private static float Confidence(string text)
    {
        if (!float.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
            !float.IsFinite(value) || value < 0 || value > 1) throw new FormatException("Invalid confidence.");
        return value;
    }

    private static JsonElement Required(JsonElement parent, string name, JsonValueKind kind)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new FormatException($"Missing or invalid response field '{name}'.");
        return value;
    }
    private static string Text(JsonElement parent, string name) => Required(parent, name, JsonValueKind.String).GetString()!;

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new FormatException("Duplicate JSON property.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }
}
