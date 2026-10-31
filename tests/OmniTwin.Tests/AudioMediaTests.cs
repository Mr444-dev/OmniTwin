using System;
using System.IO;
using System.Text;
using OmniTwin.Contracts;
using OmniTwin.Perception.Audio;
using OmniTwin.Transport;
using Xunit;

namespace OmniTwin.Tests;

public sealed class AudioMediaTests
{
    private static float[] Tone(int rate, double frequency, int channels = 1)
    {
        var data = new float[rate * channels];
        for (var i = 0; i < rate; i++) for (var channel = 0; channel < channels; channel++) data[i * channels + channel] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / rate));
        return data;
    }
    [Fact]
    public void CanonicalWaveOwnsOutputAndPreservesSignedSampleBoundaries()
    {
        var samples = new float[16000]; samples[0] = -1; samples[1] = 1;
        var wav = PcmWaveCodec.Encode(samples, 16000, 1); Assert.Equal(1000, PcmWaveCodec.Validate(wav)); Assert.Equal(32044, wav.Length);
        Assert.Equal(Encoding.ASCII.GetBytes("RIFF"), wav.AsSpan(0, 4).ToArray());
        Assert.Equal(-32767, unchecked((short)(wav[44] | wav[45] << 8))); Assert.Equal(32767, wav[46] | wav[47] << 8);
        Array.Clear(samples, 0, samples.Length); Assert.Equal(-32767, unchecked((short)(wav[44] | wav[45] << 8)));
    }
    [Theory]
    [InlineData(8000)] [InlineData(24000)] [InlineData(44100)] [InlineData(44101)] [InlineData(48000)] [InlineData(96000)]
    public void ResamplingPreservesDurationAndAudibleTone(int sourceRate)
    {
        var wav = PcmWaveCodec.Encode(Tone(sourceRate, 1000, 2), sourceRate, 2);
        Assert.Equal(1000, PcmWaveCodec.Validate(wav)); Assert.InRange(Rms(wav), 0.34, 0.37);
    }
    [Fact]
    public void DownsamplingSuppressesOutOfBandEnergyInsteadOfAliasingIntoSpeech()
    {
        var inBand = Rms(PcmWaveCodec.Encode(Tone(48000, 1000), 48000, 1));
        var outOfBand = Rms(PcmWaveCodec.Encode(Tone(48000, 12000), 48000, 1));
        Assert.True(outOfBand < inBand / 20, $"Out-of-band RMS={outOfBand}");
    }
    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(1.1f)]
    public void InvalidSamplesNeverReachUpload(float sample)
    { var data = new float[16000]; data[12] = sample; Assert.Throws<ArgumentException>(() => PcmWaveCodec.Encode(data, 16000, 1)); }
    [Fact]
    public void WaveDurationAndCompleteHeaderAreStrictlyBounded()
    {
        Assert.Throws<ArgumentException>(() => PcmWaveCodec.Encode(new float[100], 16000, 1));
        Assert.Throws<ArgumentException>(() => PcmWaveCodec.Encode(new float[80001], 16000, 1));
        var wav = PcmWaveCodec.Encode(new float[80000], 16000, 1); Assert.Equal(5000, PcmWaveCodec.Validate(wav));
        wav[22] = 2; Assert.Throws<InvalidDataException>(() => PcmWaveCodec.Validate(wav));
    }
    [Fact]
    public void FractionalMinimumSourceDurationCannotProduceASubminimumOutput()
    {
        Assert.Throws<ArgumentException>(() => PcmWaveCodec.Encode(new float[8820], 44101, 1));
        Assert.Equal(200, PcmWaveCodec.Validate(PcmWaveCodec.Encode(new float[8821], 44101, 1)));
    }
    [Fact]
    public void TranscriptSchemaPreservesPolishAndRejectsUnknownOrNoncanonicalFields()
    {
        var id = Guid.NewGuid(); var draft = new VoiceTranscription(id, "  Utwórz\n kordon\t ochronny. ", 1000);
        Assert.Equal("Utwórz kordon ochronny.", draft.Transcript);
        Assert.Equal(draft.Transcript, MediaWireCodec.DecodeTranscription(MediaWireCodec.EncodeTranscription(draft)).Transcript);
        var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"requestId\":\"" + id + "\",\"transcript\":\"hold\",\"audioDurationMilliseconds\":1000,\"extra\":true}");
        Assert.Throws<WorldStreamProtocolException>(() => MediaWireCodec.DecodeTranscription(bytes));
        Assert.Equal("", new VoiceTranscription(id, " \n ", 1000).Transcript);
        Assert.Throws<ArgumentException>(() => new VoiceTranscription(id, "bad\0text", 1000));
    }
    private static double Rms(byte[] wav)
    {
        var sum = 0.0; var count = (wav.Length - 44) / 2;
        for (var i = 44; i < wav.Length; i += 2) { var value = unchecked((short)(wav[i] | wav[i + 1] << 8)) / 32767.0; sum += value * value; }
        return Math.Sqrt(sum / count);
    }
}
