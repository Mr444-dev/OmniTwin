using System;

namespace OmniTwin.Contracts;

/// <summary>Validated visual simulation classification; this is not physical hazard identification.</summary>
public sealed class ObjectSemantics
{
    /// <summary>Creates a closed role and hazard category with bounded plain-text label.</summary>
    public ObjectSemantics(string label, string role, string hazardLevel, float confidence)
    {
        Label = ContractGuard.Text(label, 96, nameof(label));
        if (role is not ("obstacle" or "hazard" or "rescue_target" or "unknown")) throw new ArgumentException("Unknown semantic role.", nameof(role));
        if (hazardLevel is not ("none" or "caution" or "high" or "unknown")) throw new ArgumentException("Unknown hazard level.", nameof(hazardLevel));
        foreach (var character in Label) if (char.IsControl(character)) throw new ArgumentException("Label contains controls.", nameof(label));
        Role = role; HazardLevel = hazardLevel; Confidence = ContractGuard.Range(confidence, 0, 1, nameof(confidence));
    }
    /// <summary>Gets a short plain-text name.</summary>
    public string Label { get; }
    /// <summary>Gets the simulation role.</summary>
    public string Role { get; }
    /// <summary>Gets a visual simulation hazard tag.</summary>
    public string HazardLevel { get; }
    /// <summary>Gets model confidence, or one for an explicit operator choice.</summary>
    public float Confidence { get; }
    /// <summary>Gets whether the chosen simulation tag designates a hazard.</summary>
    public bool IsHazardZone => Role == "hazard" || HazardLevel is "caution" or "high";
}
