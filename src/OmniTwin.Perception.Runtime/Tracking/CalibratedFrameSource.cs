using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Perception.Vision;

namespace OmniTwin.Perception.Tracking;

/// <summary>Checks image dimensions before a capture frame can be sent for calibrated inference.</summary>
public static class CalibratedFrameSource
{
    /// <summary>Passes only frames matching the immutable calibration; mismatches fail the stream.</summary>
    public static async IAsyncEnumerable<CapturedFrame> ValidateAsync(IAsyncEnumerable<CapturedFrame> frames,
        CalibrationProfile profile, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (frames is null) throw new ArgumentNullException(nameof(frames));
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        await foreach (var frame in frames.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (frame.Width != profile.ImageWidth || frame.Height != profile.ImageHeight)
                throw new InvalidDataException("Camera dimensions do not match calibration; this frame was not submitted for inference.");
            yield return frame;
        }
    }
}
