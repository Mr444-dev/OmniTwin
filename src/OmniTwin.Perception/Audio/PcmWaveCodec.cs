using System;
using System.IO;
using OmniTwin.Contracts;

namespace OmniTwin.Perception.Audio;

/// <summary>Strict bounded PCM16/16 kHz mono WAV used for short operator commands.</summary>
public static class PcmWaveCodec
{
    /// <summary>Canonical output sample rate.</summary>
    public const int SampleRate = MediaProtocolLimits.AudioSampleRate;
    /// <summary>Maximum recorded command length.</summary>
    public const int MaximumSeconds = MediaProtocolLimits.AudioMaximumSeconds;
    /// <summary>Maximum accepted canonical WAV body.</summary>
    public const int MaximumBytes = 44 + SampleRate * MaximumSeconds * 2;
    /// <summary>Encodes finite interleaved mono/stereo samples; windowed-sinc resampling suppresses downsampling aliasing.</summary>
    public static byte[] Encode(float[] samples, int sourceRate, int channels)
    {
        if (samples is null || sourceRate < 8000 || sourceRate > 96000 || channels < 1 || channels > 2 || samples.Length % channels != 0)
            throw new ArgumentException("Unsupported PCM layout.");
        var frames = samples.Length / channels;
        if ((long)frames * 5 < sourceRate || frames > sourceRate * MaximumSeconds) throw new ArgumentException("Record 0.2 to 5 seconds.");
        foreach (var value in samples) if (float.IsNaN(value) || float.IsInfinity(value) || value < -1 || value > 1) throw new ArgumentException("PCM samples must be finite in [-1,1].");
        var count = (int)((long)frames * SampleRate / sourceRate); var bytes = new byte[44 + count * 2];
        Text(bytes, 0, "RIFF"); U32(bytes, 4, bytes.Length - 8); Text(bytes, 8, "WAVEfmt "); U32(bytes, 16, 16);
        U16(bytes, 20, 1); U16(bytes, 22, 1); U32(bytes, 24, SampleRate); U32(bytes, 28, SampleRate * 2); U16(bytes, 32, 2); U16(bytes, 34, 16);
        Text(bytes, 36, "data"); U32(bytes, 40, count * 2);
        double Mono(int frame) { var sum = 0.0; for (var channel = 0; channel < channels; channel++) sum += samples[frame * channels + channel]; return sum / channels; }
        var cutoff = Math.Min(1.0, (double)SampleRate / sourceRate) * 0.9;
        for (var i = 0; i < count; i++)
        {
            var position = (double)i * sourceRate / SampleRate; double value;
            if (sourceRate == SampleRate) value = Mono(i);
            else
            {
                var center = (int)position; var sum = 0.0; var weights = 0.0;
                for (var tap = -32; tap <= 32; tap++)
                {
                    var index = center + tap; if (index < 0 || index >= frames) continue;
                    var distance = position - index; var scaled = Math.PI * distance * cutoff;
                    var sinc = Math.Abs(scaled) < 1e-10 ? 1 : Math.Sin(scaled) / scaled;
                    var window = 0.5 + 0.5 * Math.Cos(Math.PI * distance / 33);
                    var weight = cutoff * sinc * window; sum += Mono(index) * weight; weights += weight;
                }
                value = sum / weights;
            }
            var integer = (int)Math.Round(Math.Max(-1, Math.Min(1, value)) * 32767, MidpointRounding.AwayFromZero);
            U16(bytes, 44 + i * 2, unchecked((ushort)(short)integer));
        }
        return bytes;
    }
    /// <summary>Validates the complete canonical header/data boundary before upload; returns duration in milliseconds.</summary>
    public static int Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 44 + SampleRate / 5 * 2 || bytes.Length > MaximumBytes || (bytes.Length & 1) != 0 ||
            !Is(bytes, 0, "RIFF") || !Is(bytes, 8, "WAVEfmt ") || !Is(bytes, 36, "data") ||
            R32(bytes, 4) != bytes.Length - 8 || R32(bytes, 16) != 16 || R16(bytes, 20) != 1 || R16(bytes, 22) != 1 ||
            R32(bytes, 24) != SampleRate || R32(bytes, 28) != SampleRate * 2 || R16(bytes, 32) != 2 || R16(bytes, 34) != 16 || R32(bytes, 40) != bytes.Length - 44)
            throw new InvalidDataException("Expected canonical mono PCM16 WAV at 16 kHz, lasting 0.2 to 5 seconds.");
        return (bytes.Length - 44) * 1000 / (SampleRate * 2);
    }
    private static void Text(byte[] bytes, int offset, string value) { for (var i = 0; i < value.Length; i++) bytes[offset + i] = (byte)value[i]; }
    private static bool Is(ReadOnlySpan<byte> bytes, int offset, string value) { for (var i = 0; i < value.Length; i++) if (bytes[offset + i] != value[i]) return false; return true; }
    private static void U16(byte[] bytes, int offset, int value) { bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); }
    private static void U32(byte[] bytes, int offset, int value) { U16(bytes, offset, value); U16(bytes, offset + 2, value >> 16); }
    private static int R16(ReadOnlySpan<byte> bytes, int offset) => bytes[offset] | bytes[offset + 1] << 8;
    private static int R32(ReadOnlySpan<byte> bytes, int offset) => R16(bytes, offset) | R16(bytes, offset + 2) << 16;
}
