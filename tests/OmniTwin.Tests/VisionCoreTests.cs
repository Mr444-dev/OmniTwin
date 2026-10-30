using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using OmniTwin.Perception.Vision;
using Xunit;

namespace OmniTwin.Tests;

public sealed class VisionCoreTests
{
    public static IEnumerable<object[]> OfficialMasks()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sam-one-bit.json")));
        foreach (var item in document.RootElement.EnumerateArray())
            yield return new object[] { item.GetProperty("name").GetString()!, item.GetProperty("width").GetInt32(),
                item.GetProperty("height").GetInt32(), item.GetProperty("payload").GetString()!, item.GetProperty("rasterBase64").GetString()! };
    }

    [Theory]
    [MemberData(nameof(OfficialMasks))]
    public void DecodesExactlyLikeOfficialMetaParser(string name, int width, int height, string payload, string expectedBase64)
    {
        Assert.NotEmpty(name);
        Assert.Equal(Convert.FromBase64String(expectedBase64), SamOneBitMaskCodec.Decode(payload, width, height));
        Assert.Throws<FormatException>(() => SamOneBitMaskCodec.Decode(payload + "!", width, height));
    }

    [Theory]
    [InlineData("")]
    [InlineData("~!!!!!")]
    [InlineData("!bad")]
    [InlineData("!~~~~~")]
    [InlineData("!\"!!!!!!!!!")]
    public void RejectsMalformedMasks(string payload) =>
        Assert.Throws<FormatException>(() => SamOneBitMaskCodec.Decode(payload, 1, 1));

    [Fact]
    public void RejectsOversizedMasksBeforeAllocating()
    {
        Assert.Throws<FormatException>(() => SamOneBitMaskCodec.Decode("!", int.MaxValue, int.MaxValue));
        Assert.Throws<FormatException>(() => SamOneBitMaskCodec.Decode("!", 2, 2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => SamOneBitMaskCodec.Decode("!", 1, 1, int.MaxValue));
        Assert.Throws<ArgumentNullException>(() => SamOneBitMaskCodec.Decode(null!, 1, 1));
    }

    [Fact]
    public void FrameTriggerRateLimitsMotionRefreshesAfterSettlingAndPeriodicallySyncs()
    {
        var trigger = new FrameChangeTrigger();
        Assert.True(trigger.Observe(new byte[] { 0, 0, 0, 0 }, 0).ShouldSegment);
        var moving = trigger.Observe(new byte[] { 100, 0, 0, 0 }, 100);
        Assert.Equal(0.25, moving.ChangedFraction);
        Assert.False(moving.ShouldSegment);
        Assert.True(trigger.Observe(new byte[] { 100, 0, 0, 0 }, 250).ShouldSegment);
        Assert.False(trigger.Observe(new byte[] { 100, 0, 0, 0 }, 300).ShouldSegment);
        Assert.True(trigger.Observe(new byte[] { 100, 0, 0, 0 }, 1250).ShouldSegment);
    }

    [Fact]
    public void FrameTriggerCopiesInputsAndIgnoresSubthresholdNoise()
    {
        var trigger = new FrameChangeTrigger();
        var frame = new byte[] { 0, 0 };
        trigger.Observe(frame, 0);
        frame[0] = 100;
        var result = trigger.Observe(new byte[] { 10, 10 }, 300);
        Assert.Equal(0, result.ChangedFraction);
        Assert.False(result.ShouldSegment);
    }

    [Fact]
    public void FrameTriggerRejectsTimeReversalAndResetsForSizeChanges()
    {
        var trigger = new FrameChangeTrigger();
        trigger.Observe(new byte[] { 0 }, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => trigger.Observe(new byte[] { 0 }, 99));
        Assert.True(trigger.Observe(new byte[] { 0, 0 }, 101).ShouldSegment);
        Assert.Throws<ArgumentException>(() => trigger.Observe(Array.Empty<byte>(), 102));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameChangeTrigger(changedFraction: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameChangeTrigger(syncIntervalMs: 1));
    }
}
