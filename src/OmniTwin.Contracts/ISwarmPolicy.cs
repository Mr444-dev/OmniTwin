using System.Collections.Generic;

namespace OmniTwin.Contracts;

/// <summary>Computes tactical targets from immutable observations in a supervised execution context.</summary>
/// <remarks>Generated implementations must execute outside the host and Unity process under OS isolation.</remarks>
public interface ISwarmPolicy
{
    /// <summary>Gets a concise, stable display name.</summary>
    string PolicyName { get; }

    /// <summary>Computes targets for a finite, nonnegative monotonic simulation time in seconds.</summary>
    /// <remarks>Callers must validate output IDs, count, finite values, workspace bounds, speed, and collision margins.</remarks>
    IReadOnlyList<AgentTargetDirective> ComputeDirectives(WorldStateSnapshot worldState, float timeSeconds);
}
