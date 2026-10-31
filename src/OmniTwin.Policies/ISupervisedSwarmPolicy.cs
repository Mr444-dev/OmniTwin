using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;

namespace OmniTwin.Policies;

/// <summary>An asynchronous execution boundary; generated implementations run behind OS-enforced isolation.</summary>
public interface ISupervisedSwarmPolicy
{
    /// <summary>Gets the verified policy name.</summary>
    string PolicyName { get; }
    /// <summary>Computes one bounded set of targets without executing untrusted code in the caller process.</summary>
    Task<IReadOnlyList<AgentTargetDirective>> ComputeAsync(WorldStateSnapshot world, float timeSeconds, CancellationToken cancellationToken);
}

/// <summary>A sanitized policy-worker failure that selects a built-in policy instead of escaping to the simulation.</summary>
public sealed class PolicyExecutionException : Exception
{
    /// <summary>Creates a bounded failure category.</summary>
    public PolicyExecutionException(string category) : base(category)
    { if (string.IsNullOrWhiteSpace(category) || category.Length > 128) throw new ArgumentException("A bounded category is required.", nameof(category)); Category = category; }
    /// <summary>Gets the safe execution category.</summary>
    public string Category { get; }
}
