using System;

namespace OmniTwin.Transport;

/// <summary>Bounded local metadata for the tactical HUD model and sandbox readiness matrix.</summary>
public sealed class BackendModelReadiness
{
    /// <summary>Creates an immutable, secret-free status payload.</summary>
    public BackendModelReadiness(string samModelId, string museSemanticModelId, string museVoiceModelId,
        string museImageModelId, string basetenRequiredModelId, string basetenAliasModelId,
        bool metaApiKeyConfigured, bool basetenApiKeyConfigured, bool windowsAppContainerSupported, bool policyWorkerPresent)
    {
        SamModelId = Text(samModelId, nameof(samModelId)); MuseSemanticModelId = Text(museSemanticModelId, nameof(museSemanticModelId));
        MuseVoiceModelId = Text(museVoiceModelId, nameof(museVoiceModelId)); MuseImageModelId = Text(museImageModelId, nameof(museImageModelId));
        BasetenRequiredModelId = Text(basetenRequiredModelId, nameof(basetenRequiredModelId)); BasetenAliasModelId = Text(basetenAliasModelId, nameof(basetenAliasModelId));
        MetaApiKeyConfigured = metaApiKeyConfigured; BasetenApiKeyConfigured = basetenApiKeyConfigured;
        WindowsAppContainerSupported = windowsAppContainerSupported; PolicyWorkerPresent = policyWorkerPresent;
    }
    /// <summary>Gets the SAM 3.1 model identifier.</summary>
    public string SamModelId { get; }
    /// <summary>Gets the semantic Muse Spark model identifier.</summary>
    public string MuseSemanticModelId { get; }
    /// <summary>Gets the Muse Voice model identifier.</summary>
    public string MuseVoiceModelId { get; }
    /// <summary>Gets the Muse Image model identifier.</summary>
    public string MuseImageModelId { get; }
    /// <summary>Gets the required dated Baseten model identifier.</summary>
    public string BasetenRequiredModelId { get; }
    /// <summary>Gets the authorized Baseten alias identifier.</summary>
    public string BasetenAliasModelId { get; }
    /// <summary>Gets whether the Meta credential exists, without exposing it.</summary>
    public bool MetaApiKeyConfigured { get; }
    /// <summary>Gets whether the Baseten credential exists, without exposing it.</summary>
    public bool BasetenApiKeyConfigured { get; }
    /// <summary>Gets whether AppContainer is supported on this backend OS.</summary>
    public bool WindowsAppContainerSupported { get; }
    /// <summary>Gets whether the isolated policy worker assembly is present.</summary>
    public bool PolicyWorkerPresent { get; }
    private static string Text(string value, string name) => string.IsNullOrWhiteSpace(value) || value.Length > 128
        ? throw new ArgumentException("A bounded model identifier is required.", name) : value;
}
