using System;
using Microsoft.Extensions.Configuration;
using OmniTwin.Contracts;

namespace OmniTwin.Backend;

/// <summary>Validated loopback-host and perception settings loaded from the OmniTwin configuration section.</summary>
public sealed class BackendOptions
{
    /// <summary>Creates host settings. Port zero asks the OS for an ephemeral loopback port.</summary>
    public BackendOptions(int port = 5108, bool enablePerception = false, int cameraDevice = 0,
        string? calibrationPath = null, string concept = "cup", bool hazard = false, int maximumClients = 16,
        int sendTimeoutMilliseconds = 2000, int maximumPublicationAgeMilliseconds = 2000, int retentionMilliseconds = PerceptionTiming.VerifiedRetentionMilliseconds)
    {
        if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (cameraDevice < 0) throw new ArgumentOutOfRangeException(nameof(cameraDevice));
        if (maximumClients < 1 || maximumClients > 128) throw new ArgumentOutOfRangeException(nameof(maximumClients));
        if (sendTimeoutMilliseconds < 50 || sendTimeoutMilliseconds > 30_000) throw new ArgumentOutOfRangeException(nameof(sendTimeoutMilliseconds));
        if (maximumPublicationAgeMilliseconds < 50 || maximumPublicationAgeMilliseconds > 60_000) throw new ArgumentOutOfRangeException(nameof(maximumPublicationAgeMilliseconds));
        if (retentionMilliseconds < 1 || retentionMilliseconds > 60_000) throw new ArgumentOutOfRangeException(nameof(retentionMilliseconds));
        if (string.IsNullOrWhiteSpace(concept) || concept.Length > 128 || concept.IndexOfAny(new[] { '\r', '\n', '<', '>', ',', ';' }) >= 0)
            throw new ArgumentException("Use one bounded noun phrase.", nameof(concept));
        if (enablePerception && string.IsNullOrWhiteSpace(calibrationPath)) throw new ArgumentException("Enabled perception requires a calibration file.", nameof(calibrationPath));
        Port = port; EnablePerception = enablePerception; CameraDevice = cameraDevice; CalibrationPath = calibrationPath;
        Concept = concept; Hazard = hazard; MaximumClients = maximumClients; SendTimeoutMilliseconds = sendTimeoutMilliseconds;
        MaximumPublicationAgeMilliseconds = maximumPublicationAgeMilliseconds; RetentionMilliseconds = retentionMilliseconds;
    }
    /// <summary>Gets the local HTTP port.</summary>
    public int Port { get; }
    /// <summary>Gets whether camera/model processing is explicitly enabled.</summary>
    public bool EnablePerception { get; }
    /// <summary>Gets the local capture device index.</summary>
    public int CameraDevice { get; }
    /// <summary>Gets the measured calibration file path, required when enabled.</summary>
    public string? CalibrationPath { get; }
    /// <summary>Gets the queried SAM concept.</summary>
    public string Concept { get; }
    /// <summary>Gets the operator-defined hazard flag.</summary>
    public bool Hazard { get; }
    /// <summary>Gets the simultaneous WebSocket subscriber limit.</summary>
    public int MaximumClients { get; }
    /// <summary>Gets the per-send WebSocket deadline.</summary>
    public int SendTimeoutMilliseconds { get; }
    /// <summary>Gets how long the latest cached publication can be replayed.</summary>
    public int MaximumPublicationAgeMilliseconds { get; }
    /// <summary>Gets source/track retention shared by the tracker and transport envelope.</summary>
    public int RetentionMilliseconds { get; }

    /// <summary>Reads explicit settings; invalid configured values fail instead of silently using a fallback.</summary>
    public static BackendOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("OmniTwin");
        return new BackendOptions(section.GetValue("Port", 5108), section.GetValue("EnablePerception", false),
            section.GetValue("CameraDevice", 0), section["CalibrationPath"], section["Concept"] ?? "cup",
            section.GetValue("Hazard", false), section.GetValue("MaximumClients", 16), section.GetValue("SendTimeoutMilliseconds", 2000),
            section.GetValue("MaximumPublicationAgeMilliseconds", 2000), section.GetValue("RetentionMilliseconds", PerceptionTiming.VerifiedRetentionMilliseconds));
    }
}
