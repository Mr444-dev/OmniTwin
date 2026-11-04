using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Perception.Calibration;

namespace OmniTwin.Perception.Tracking;

/// <summary>An immutable, image-size-specific calibration and bottom-span projection configuration.</summary>
public sealed class CalibrationProfile
{
    /// <summary>Creates a calibrated workspace with finite radius limits, clearance, and confidence threshold.</summary>
    public CalibrationProfile(Guid calibrationId, int imageWidth, int imageHeight,
        IReadOnlyList<PixelPoint> cameraCorners, IReadOnlyList<Vector2D> worldCorners,
        float minimumRadiusMeters = 0.05f, float maximumRadiusMeters = 3, float safetyMarginMeters = 0.1f,
        float minimumConfidence = 0.3f, IReadOnlyList<PixelPoint>? workspaceBoundary = null)
    {
        if (calibrationId == Guid.Empty) throw new ArgumentException("Calibration ID cannot be empty.", nameof(calibrationId));
        if (imageWidth <= 0 || imageHeight <= 0 || (long)imageWidth * imageHeight > 4_194_304)
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (!float.IsFinite(minimumRadiusMeters) || minimumRadiusMeters <= 0 || !float.IsFinite(maximumRadiusMeters) ||
            maximumRadiusMeters < minimumRadiusMeters || !float.IsFinite(safetyMarginMeters) || safetyMarginMeters < 0 ||
            (double)minimumRadiusMeters + safetyMarginMeters > maximumRadiusMeters ||
            !float.IsFinite(minimumConfidence) || minimumConfidence < 0 || minimumConfidence > 1)
            throw new ArgumentException("Invalid projection radius, margin, or confidence limits.");
        if (cameraCorners is null) throw new ArgumentNullException(nameof(cameraCorners));
        foreach (var point in cameraCorners)
            if (point.U < 0 || point.V < 0 || point.U > imageWidth - 1 || point.V > imageHeight - 1)
                throw new ArgumentException("Calibration corners must be pixel centers inside the configured image.", nameof(cameraCorners));
        if (workspaceBoundary is not null)
            foreach (var point in workspaceBoundary)
                if (point.U < 0 || point.V < 0 || point.U > imageWidth - 1 || point.V > imageHeight - 1)
                    throw new ArgumentException("Workspace boundary points must be pixel centers inside the configured image.", nameof(workspaceBoundary));
        Workspace = new CalibratedWorkspace(cameraCorners, worldCorners, workspaceBoundary);
        CalibrationId = calibrationId; ImageWidth = imageWidth; ImageHeight = imageHeight;
        MinimumRadiusMeters = minimumRadiusMeters; MaximumRadiusMeters = maximumRadiusMeters;
        SafetyMarginMeters = safetyMarginMeters; MinimumConfidence = minimumConfidence;
    }
    /// <summary>Gets calibration identity, which changes when the camera/plane mapping changes.</summary>
    public Guid CalibrationId { get; }
    /// <summary>Gets the calibrated source image width.</summary>
    public int ImageWidth { get; }
    /// <summary>Gets the calibrated source image height.</summary>
    public int ImageHeight { get; }
    /// <summary>Gets the immutable workspace and mapper.</summary>
    public CalibratedWorkspace Workspace { get; }
    /// <summary>Gets the minimum estimated radius before clearance is added.</summary>
    public float MinimumRadiusMeters { get; }
    /// <summary>Gets the maximum accepted radius including clearance.</summary>
    public float MaximumRadiusMeters { get; }
    /// <summary>Gets the additional radial clearance in world meters.</summary>
    public float SafetyMarginMeters { get; }
    /// <summary>Gets the minimum accepted detector confidence.</summary>
    public float MinimumConfidence { get; }
    /// <summary>Gets the perimeter-ordered pixel boundary, including optional extra vertices used for irregular tabletops.</summary>
    public IReadOnlyList<PixelPoint> WorkspaceBoundary => Workspace.CameraBoundary;

    /// <summary>Loads a closed-schema calibration JSON file with a 16 KiB read limit.</summary>
    public static async Task<CalibrationProfile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var bytes = new byte[16_385];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > 16_384) throw new InvalidDataException("Calibration file exceeds 16 KiB.");
        return Parse(bytes.AsMemory(0, count));
    }

    /// <summary>Parses all required fields, rejecting unknown/duplicate keys, nonfinite numbers, and unsupported versions.</summary>
    public static CalibrationProfile Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length > 16_384) throw new InvalidDataException("Calibration JSON exceeds 16 KiB.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        CheckRootKeys(root);
        if (Integer(root, "schemaVersion") != 1) throw new FormatException("Unsupported calibration schema version.");
        var id = root.GetProperty("calibrationId");
        if (id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var calibrationId))
            throw new FormatException("Invalid calibration ID.");
        var camera = Corners(root, "cameraCorners"); var world = Corners(root, "worldCorners");
        var pixels = new PixelPoint[4]; var meters = new Vector2D[4];
        for (var i = 0; i < 4; i++)
        {
            CheckKeys(camera[i], "u", "v"); CheckKeys(world[i], "x", "z");
            pixels[i] = new PixelPoint(Number(camera[i], "u"), Number(camera[i], "v"));
            meters[i] = new Vector2D(Scalar(world[i], "x"), Scalar(world[i], "z"));
        }
        PixelPoint[]? boundary = null;
        if (root.TryGetProperty("workspaceBoundary", out var boundaryElement))
        {
            if (boundaryElement.ValueKind != JsonValueKind.Array || boundaryElement.GetArrayLength() is < 4 or > 64)
                throw new FormatException("Workspace boundary must contain 4 to 64 points.");
            boundary = new PixelPoint[boundaryElement.GetArrayLength()];
            var index = 0;
            foreach (var point in boundaryElement.EnumerateArray())
            {
                CheckKeys(point, "u", "v");
                boundary[index++] = new PixelPoint(Number(point, "u"), Number(point, "v"));
            }
        }
        return new CalibrationProfile(calibrationId, Integer(root, "imageWidth"), Integer(root, "imageHeight"), pixels, meters,
            Scalar(root, "minimumRadiusMeters"), Scalar(root, "maximumRadiusMeters"), Scalar(root, "safetyMarginMeters"),
            Scalar(root, "minimumConfidence"), boundary);
    }

    private static void CheckRootKeys(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a calibration object.");
        string[] required = { "schemaVersion", "calibrationId", "imageWidth", "imageHeight", "cameraCorners", "worldCorners",
            "minimumRadiusMeters", "maximumRadiusMeters", "safetyMarginMeters", "minimumConfidence" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if ((!Array.Exists(required, key => key == property.Name) && property.Name != "workspaceBoundary") || !seen.Add(property.Name))
                throw new FormatException("Unknown or duplicate calibration property.");
        foreach (var key in required)
            if (!seen.Contains(key)) throw new FormatException("Missing calibration property.");
    }

    private static JsonElement Corners(JsonElement root, string key)
    {
        var value = root.GetProperty(key);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4) throw new FormatException("Exactly four corners are required.");
        return value;
    }
    private static int Integer(JsonElement root, string key)
    {
        var value = root.GetProperty(key);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw new FormatException($"Invalid integer '{key}'.");
        return number;
    }
    private static double Number(JsonElement root, string key)
    {
        var value = root.GetProperty(key);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new FormatException($"Invalid finite number '{key}'.");
        return number;
    }
    private static float Scalar(JsonElement root, string key)
    {
        var number = Number(root, key);
        if (Math.Abs(number) > float.MaxValue) throw new FormatException($"Value '{key}' exceeds float range.");
        return (float)number;
    }
    private static void CheckKeys(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a calibration object.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (Array.IndexOf(expected, property.Name) < 0 || !keys.Add(property.Name))
                throw new FormatException("Unknown or duplicate calibration property.");
        if (keys.Count != expected.Length) throw new FormatException("Missing calibration property.");
    }
}
