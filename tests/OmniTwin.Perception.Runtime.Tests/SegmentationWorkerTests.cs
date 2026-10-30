using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Perception.Vision;
using Xunit;

namespace OmniTwin.Perception.Runtime.Tests;

public sealed class SegmentationWorkerTests
{
    private static readonly DateTimeOffset InitialTime = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static CapturedFrame Frame(long sequence, DateTimeOffset? time = null, bool shouldSegment = true) =>
        new(sequence, time ?? InitialTime, 2, 3, new byte[] { 255, 216, 255, 217 }, new FrameChangeDecision(0, shouldSegment));
    private static SamSegmentationResult Result(CapturedFrame frame) => new("resp_fixture", frame, 0, Array.Empty<SamDetection>());

    [Fact]
    public async Task KeepsOnlyLatestPendingFrameAndNeverOverlapsCalls()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<long>();
        var active = 0;
        var client = new Client(async (frame, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            calls.Add(frame.SequenceNumber);
            if (frame.SequenceNumber == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
            Interlocked.Decrement(ref active);
            return Result(frame);
        });
        async IAsyncEnumerable<CapturedFrame> Source([EnumeratorCancellation] CancellationToken token = default)
        {
            yield return Frame(1);
            await entered.Task.WaitAsync(token);
            yield return Frame(2);
            yield return Frame(3);
            yield return Frame(4);
            published.SetResult();
        }
        var worker = new SegmentationWorker(client, timeProvider: new Clock());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = worker.RunAsync(Source(timeout.Token), "cup", timeout.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var first = reader.MoveNextAsync().AsTask();
        await published.Task.WaitAsync(timeout.Token);
        release.SetResult();
        Assert.True(await first);
        Assert.Equal(1, reader.Current.SequenceNumber);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(4, reader.Current.SequenceNumber);
        Assert.False(await reader.MoveNextAsync());
        Assert.Equal(new long[] { 1, 4 }, calls);
    }

    [Fact]
    public async Task RejectsOldOrFutureFramesBeforeCallingApi()
    {
        var calls = 0;
        var worker = new SegmentationWorker(new Client((frame, _) => { calls++; return Task.FromResult(Result(frame)); }), timeProvider: new Clock());
        foreach (var time in new[] { InitialTime.AddSeconds(-6), InitialTime.AddSeconds(1) })
        {
            await foreach (var update in worker.RunAsync(One(Frame(1, time)), "cup", TestContext.Current.CancellationToken))
            {
                Assert.Equal("stale_frame", update.FailureCategory);
                Assert.Null(update.Result);
            }
        }
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DiscardsResultsThatBecomeStaleDuringInference()
    {
        var clock = new Clock();
        var client = new Client((frame, _) => { clock.Now = InitialTime.AddSeconds(6); return Task.FromResult(Result(frame)); });
        var worker = new SegmentationWorker(client, timeProvider: clock);
        await foreach (var update in worker.RunAsync(One(Frame(1)), "cup", TestContext.Current.CancellationToken))
        {
            Assert.Null(update.Result);
            Assert.Equal("stale_result", update.FailureCategory);
        }
    }

    [Fact]
    public async Task ReportsApiFailureAndIgnoresUnchangedFrames()
    {
        var calls = 0;
        var worker = new SegmentationWorker(new Client((_, _) => { calls++; throw new SamApiException("attempt_timeout", 3); }), timeProvider: new Clock());
        var updates = 0;
        await foreach (var update in worker.RunAsync(One(Frame(1)), "cup", TestContext.Current.CancellationToken))
        { updates++; Assert.Equal("attempt_timeout", update.FailureCategory); }
        Assert.Equal(1, updates);
        await foreach (var _ in worker.RunAsync(One(Frame(2, shouldSegment: false)), "cup", TestContext.Current.CancellationToken)) updates++;
        Assert.Equal(1, updates);
        Assert.Equal(1, calls);
    }

    private static async IAsyncEnumerable<CapturedFrame> One(CapturedFrame frame)
    {
        await Task.Yield();
        yield return frame;
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = InitialTime;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Client : ISam31Client
    {
        private readonly Func<CapturedFrame, CancellationToken, Task<SamSegmentationResult>> _segment;
        internal Client(Func<CapturedFrame, CancellationToken, Task<SamSegmentationResult>> segment) => _segment = segment;
        public Task<SamSegmentationResult> SegmentAsync(CapturedFrame frame, string nounPhrase, CancellationToken cancellationToken = default) =>
            _segment(frame, cancellationToken);
    }
}
