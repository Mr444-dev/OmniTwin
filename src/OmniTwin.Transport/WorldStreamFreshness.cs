using System;
using OmniTwin.Contracts;

namespace OmniTwin.Transport;

/// <summary>Checks a received publication without relying solely on the server's publication frequency.</summary>
public static class WorldStreamFreshness
{
    /// <summary>Combines source age, delivery age, local cache age, failures, and rejection diagnostics.</summary>
    /// <remarks>Both local monotonic times must share a clock; wall clocks must be within the allowed skew.</remarks>
    public static bool IsObservationCurrent(WorldStreamMessage message, long receivedElapsedMs, long nowElapsedMs,
        long receivedAtUnixMs, long maximumSilenceMilliseconds = 2000, long allowedClockSkewMilliseconds = 500)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        if (receivedElapsedMs < 0 || nowElapsedMs < receivedElapsedMs || receivedAtUnixMs < 0 ||
            maximumSilenceMilliseconds < 1 || allowedClockSkewMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(nowElapsedMs));
        var localAge = nowElapsedMs - receivedElapsedMs;
        var deliveryAge = receivedAtUnixMs - message.SentAtUnixMs;
        if (localAge >= maximumSilenceMilliseconds || deliveryAge < -allowedClockSkewMilliseconds ||
            !message.World.IsObservationCurrent || message.FailureCategory is not null || message.RejectedDetectionCount != 0)
            return false;
        var sourceAge = message.World.SourceAgeMilliseconds!.Value;
        var remaining = message.RetentionMilliseconds - sourceAge;
        var delivery = Math.Max(0, deliveryAge);
        foreach (var track in message.World.Obstacles)
            if (track.ObservationSource == ObservationSource.LocalMotionEstimate &&
                (track.GeometryAgeMilliseconds >= PerceptionTiming.LocalMotionAgeMilliseconds ||
                delivery + localAge >= PerceptionTiming.LocalMotionAgeMilliseconds - track.GeometryAgeMilliseconds)) return false;
        return delivery < remaining && localAge < remaining - delivery;
    }
}
