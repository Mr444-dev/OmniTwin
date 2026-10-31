namespace OmniTwin.Contracts;

/// <summary>Shared bounds at the local audio/image boundary; no device or provider dependency.</summary>
public static class MediaProtocolLimits
{
    /// <summary>Command PCM sample rate.</summary>
    public const int AudioSampleRate = 16000;
    /// <summary>Maximum command recording duration.</summary>
    public const int AudioMaximumSeconds = 5;
    /// <summary>Maximum canonical WAV upload size.</summary>
    public const int AudioMaximumBytes = 44 + AudioSampleRate * AudioMaximumSeconds * 2;
    /// <summary>Maximum preview JPEG body size.</summary>
    public const int PreviewMaximumBytes = 131072;
    /// <summary>Maximum width or height decoded by the preview UI.</summary>
    public const int PreviewMaximumSide = 320;
    /// <summary>Capture-to-display lifetime; expired images must disappear.</summary>
    public const int PreviewFreshnessMilliseconds = 750;
}
