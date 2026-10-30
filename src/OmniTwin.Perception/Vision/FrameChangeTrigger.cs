using System;

namespace OmniTwin.Perception.Vision;

/// <summary>The measured fraction of changed pixels and the segmentation scheduling decision.</summary>
public readonly struct FrameChangeDecision
{
    /// <summary>Creates a scheduling result.</summary>
    public FrameChangeDecision(double changedFraction, bool shouldSegment)
    {
        ChangedFraction = changedFraction;
        ShouldSegment = shouldSegment;
    }
    /// <summary>Gets the fraction of pixels differing by at least the configured intensity threshold.</summary>
    public double ChangedFraction { get; }
    /// <summary>Gets whether the current frame should replace the pending segmentation frame.</summary>
    public bool ShouldSegment { get; }
}

/// <summary>Single-producer frame-difference trigger with rate limiting, settled-motion refresh, and periodic sync.</summary>
/// <remarks>Use one instance per capture session. Times are monotonic elapsed milliseconds, not wall-clock time.</remarks>
public sealed class FrameChangeTrigger
{
    private readonly int _pixelDelta;
    private readonly double _fraction;
    private readonly long _minimumInterval;
    private readonly long _syncInterval;
    private readonly long _settleInterval;
    private byte[]? _previous;
    private long _lastFrame = -1;
    private long _lastRequest;
    private long _lastMotion;
    private bool _settlePending;

    /// <summary>Configures intensity delta, changed-pixel fraction, minimum call spacing, periodic sync, and settling delay.</summary>
    public FrameChangeTrigger(int pixelDelta = 20, double changedFraction = 0.02,
        long minimumIntervalMs = 250, long syncIntervalMs = 1000, long settleIntervalMs = 100)
    {
        if (pixelDelta < 1 || pixelDelta > 255) throw new ArgumentOutOfRangeException(nameof(pixelDelta));
        if (double.IsNaN(changedFraction) || changedFraction <= 0 || changedFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(changedFraction));
        if (minimumIntervalMs < 1) throw new ArgumentOutOfRangeException(nameof(minimumIntervalMs));
        if (syncIntervalMs < minimumIntervalMs) throw new ArgumentOutOfRangeException(nameof(syncIntervalMs));
        if (settleIntervalMs < 0) throw new ArgumentOutOfRangeException(nameof(settleIntervalMs));
        _pixelDelta = pixelDelta;
        _fraction = changedFraction;
        _minimumInterval = minimumIntervalMs;
        _syncInterval = syncIntervalMs;
        _settleInterval = settleIntervalMs;
    }

    /// <summary>Copies the current grayscale frame and decides whether to enqueue it for segmentation.</summary>
    public FrameChangeDecision Observe(ReadOnlySpan<byte> grayscale, long elapsedMilliseconds)
    {
        if (grayscale.Length == 0) throw new ArgumentException("A grayscale frame cannot be empty.", nameof(grayscale));
        if (elapsedMilliseconds < 0 || elapsedMilliseconds < _lastFrame)
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds), "Capture time cannot move backwards.");
        _lastFrame = elapsedMilliseconds;
        if (_previous is null || _previous.Length != grayscale.Length)
        {
            _previous = grayscale.ToArray();
            _lastRequest = elapsedMilliseconds;
            _settlePending = false;
            return new FrameChangeDecision(1, true);
        }
        var changed = 0;
        for (var i = 0; i < grayscale.Length; i++)
        {
            if (Math.Abs(grayscale[i] - _previous[i]) >= _pixelDelta) changed++;
            _previous[i] = grayscale[i];
        }
        var fraction = (double)changed / grayscale.Length;
        var moving = fraction >= _fraction;
        if (moving)
        {
            _lastMotion = elapsedMilliseconds;
            _settlePending = true;
        }
        var sinceRequest = elapsedMilliseconds - _lastRequest;
        var shouldSegment = sinceRequest >= _syncInterval ||
            (sinceRequest >= _minimumInterval && (moving || (_settlePending && elapsedMilliseconds - _lastMotion >= _settleInterval)));
        if (shouldSegment)
        {
            _lastRequest = elapsedMilliseconds;
            if (!moving) _settlePending = false;
        }
        return new FrameChangeDecision(fraction, shouldSegment);
    }
}
