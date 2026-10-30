using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OmniTwin.Perception.Vision;

/// <summary>A segmentation success or sanitized operational failure for one captured frame.</summary>
public sealed class SegmentationUpdate
{
    internal SegmentationUpdate(long sequenceNumber, SamSegmentationResult? result, string? failureCategory)
    {
        SequenceNumber = sequenceNumber; Result = result; FailureCategory = failureCategory;
    }
    /// <summary>Gets the capture sequence.</summary>
    public long SequenceNumber { get; }
    /// <summary>Gets a validated, sufficiently fresh result, or null on failure.</summary>
    public SamSegmentationResult? Result { get; }
    /// <summary>Gets the failure category, or null on success.</summary>
    public string? FailureCategory { get; }
}

/// <summary>Maintains a latest-frame queue while one segmentation operation runs; no camera read waits for inference.</summary>
public sealed class SegmentationWorker
{
    private readonly ISam31Client _client;
    private readonly TimeSpan _maximumAge;
    private readonly TimeProvider _time;

    /// <summary>Configures a client and maximum observation age, checked before and after inference.</summary>
    public SegmentationWorker(ISam31Client client, TimeSpan? maximumFrameAge = null, TimeProvider? timeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _maximumAge = maximumFrameAge ?? TimeSpan.FromSeconds(5);
        if (_maximumAge <= TimeSpan.Zero || _maximumAge > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(maximumFrameAge));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Consumes changed/periodic frames, coalesces pending work, and reports API failures while capture continues.</summary>
    public async IAsyncEnumerable<SegmentationUpdate> RunAsync(IAsyncEnumerable<CapturedFrame> frames, string nounPhrase,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (frames is null) throw new ArgumentNullException(nameof(frames));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
        var producer = PumpAsync();
        async Task PumpAsync()
        {
            Exception? failure = null;
            try
            {
                await foreach (var frame in frames.WithCancellation(stop.Token).ConfigureAwait(false))
                    if (frame.Change.ShouldSegment) pending.Writer.TryWrite(frame);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error) { failure = error; }
            finally { pending.Writer.TryComplete(failure); }
        }
        try
        {
            await foreach (var frame in pending.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                SegmentationUpdate update;
                if (!IsFresh(frame)) update = new SegmentationUpdate(frame.SequenceNumber, null, "stale_frame");
                else
                {
                    try
                    {
                        var result = await _client.SegmentAsync(frame, nounPhrase, cancellationToken).ConfigureAwait(false);
                        update = IsFresh(frame) ? new SegmentationUpdate(frame.SequenceNumber, result, null)
                            : new SegmentationUpdate(frame.SequenceNumber, null, "stale_result");
                    }
                    catch (SamApiException error)
                    { update = new SegmentationUpdate(frame.SequenceNumber, null, error.Category); }
                }
                yield return update;
            }
        }
        finally { stop.Cancel(); await producer.ConfigureAwait(false); }
    }

    private bool IsFresh(CapturedFrame frame)
    {
        var age = _time.GetUtcNow() - frame.CapturedAt;
        return age >= TimeSpan.Zero && age <= _maximumAge;
    }
}
