using System;
using System.IO;
using OmniTwin.Brain;
using OmniTwin.Perception.Audio;
using OmniTwin.Perception.Models;

namespace OmniTwin.Backend.Services;

/// <summary>Safe local readiness data; contains credential presence only and never credential values.</summary>
public sealed class BackendDiagnosticsSnapshot
{
    /// <summary>Creates model and worker readiness metadata for the local diagnostics route.</summary>
    public BackendDiagnosticsSnapshot(bool metaApiKeyConfigured, bool basetenApiKeyConfigured,
        bool windowsAppContainerSupported, bool policyWorkerPresent)
    {
        MetaApiKeyConfigured = metaApiKeyConfigured;
        BasetenApiKeyConfigured = basetenApiKeyConfigured;
        WindowsAppContainerSupported = windowsAppContainerSupported;
        PolicyWorkerPresent = policyWorkerPresent;
    }

    /// <summary>Gets the diagnostics payload schema version.</summary>
    public int SchemaVersion => 1;
    /// <summary>Gets the exact SAM 3.1 model identifier used by the perception adapter.</summary>
    public string SamModelId => "sam-3.1";
    /// <summary>Gets the Muse Spark semantic model identifier.</summary>
    public string MuseSemanticModelId => MuseSparkSemanticClient.ModelId;
    /// <summary>Gets the Muse Voice transcription model identifier.</summary>
    public string MuseVoiceModelId => MuseVoiceClient.ModelId;
    /// <summary>Gets the Muse Image generation model identifier.</summary>
    public string MuseImageModelId => MuseImageClient.ModelId;
    /// <summary>Gets the required first Baseten model identifier.</summary>
    public string BasetenRequiredModelId => MuseSparkOpenClient.RequiredModel;
    /// <summary>Gets the authorized Baseten alias used only after the first model returns HTTP 404.</summary>
    public string BasetenAliasModelId => MuseSparkOpenClient.AliasModel;
    /// <summary>Gets whether META_API_KEY is set; its contents are never returned.</summary>
    public bool MetaApiKeyConfigured { get; }
    /// <summary>Gets whether BASETEN_API_KEY is set; its contents are never returned.</summary>
    public bool BasetenApiKeyConfigured { get; }
    /// <summary>Gets whether this operating system supports the Windows AppContainer path.</summary>
    public bool WindowsAppContainerSupported { get; }
    /// <summary>Gets whether the trusted isolated worker assembly exists beside the backend.</summary>
    public bool PolicyWorkerPresent { get; }

    /// <summary>Captures current provider configuration and worker prerequisites without making paid requests.</summary>
    public static BackendDiagnosticsSnapshot Capture()
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "PolicyWorker", "OmniTwin.PolicyWorker.dll");
        return new BackendDiagnosticsSnapshot(Present("META_API_KEY"), Present("BASETEN_API_KEY"),
            OperatingSystem.IsWindows(), File.Exists(workerPath));
    }

    private static bool Present(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
}
