namespace OmniTwin.Contracts;

/// <summary>Shared default budgets; image motion never renews the age of semantic verification.</summary>
public static class PerceptionTiming
{
    /// <summary>Maximum time of one SAM HTTP attempt.</summary>
    public const int SamAttemptMilliseconds = 6000;
    /// <summary>Maximum time of a complete SAM operation, including retries.</summary>
    public const int SamOperationMilliseconds = 12000;
    /// <summary>Capture-age ceiling for submitting or completing inference.</summary>
    public const int MaximumFrameAgeMilliseconds = 12000;
    /// <summary>Retention of previously verified stationary footprints.</summary>
    public const int VerifiedRetentionMilliseconds = 20000;
    /// <summary>Maximum age of a local image-motion measurement used for movement.</summary>
    public const int LocalMotionAgeMilliseconds = 1000;
}
