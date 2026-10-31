namespace OmniTwin.Contracts;

/// <summary>Identifies how a perception stream was acquired for explicit trust labeling.</summary>
public enum PerceptionSourceMode
{
    /// <summary>Camera frames processed through the configured live SAM and local tracking pipeline.</summary>
    LiveCamera = 0,
    /// <summary>Deterministic generated frames segmented locally; never evidence of a live camera or SAM result.</summary>
    SyntheticTabletopReplay = 1
}
