using System.Threading;

namespace OmniTwin.Contracts;

/// <summary>Thread-safe monotonic identity for latest-command-wins asynchronous work.</summary>
public sealed class CommandEpoch
{
    private long _value;

    /// <summary>Advances the current epoch and returns the identity owned by the new command.</summary>
    public long Advance() => Interlocked.Increment(ref _value);

    /// <summary>Returns whether a captured epoch still owns the right to publish a result.</summary>
    public bool IsCurrent(long epoch) => epoch > 0 && Volatile.Read(ref _value) == epoch;
}
