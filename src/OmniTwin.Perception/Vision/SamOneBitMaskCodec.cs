// Copyright (c) Meta Platforms, Inc. and affiliates. All Rights Reserved.
// C# adaptation of the one_bit codec in meta-sam-parser 0.1.0.
// Distributed under the SAM License; see third-party/meta-sam/LICENSE.
using System;
using System.Collections.Generic;
using System.Text;

namespace OmniTwin.Perception.Vision;

/// <summary>Decodes SAM's canonical base85/range-coded one_bit masks with explicit allocation limits.</summary>
/// <remarks>Only the requested one_bit format is accepted; lossless or malformed payloads fail explicitly.</remarks>
public static class SamOneBitMaskCodec
{
    private static readonly string Alphabet = CreateAlphabet();

    /// <summary>Returns row-major 0/1 pixels after validating encoding, dimensions, finalization, and canonical form.</summary>
    public static byte[] Decode(string payload, int width, int height, int maximumPixels = 1_048_576)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        if (maximumPixels <= 0 || maximumPixels > 16_777_216) throw new ArgumentOutOfRangeException(nameof(maximumPixels));
        if (width <= 0 || height <= 0 || (long)width * height > maximumPixels)
            throw new FormatException("Mask dimensions exceed the permitted area.");
        if (payload.Length == 0 || payload[0] != '!') throw new FormatException("Expected a one_bit mask.");
        var bytes = Unpack(payload.Substring(1), checked(maximumPixels * 2 + 16));
        var decoder = new RangeDecoder(bytes);
        var counts = new int[8192];
        for (var i = 0; i < counts.Length; i++) counts[i] = 1;
        var stride = checked(width + 4);
        var padded = new byte[checked(stride * (height + 2))];
        var raster = new byte[checked(width * height)];
        var encoder = new RangeEncoder();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var position = (y + 2) * stride + x + 2;
            var context = Context(padded, position, stride) * 2;
            var zero = counts[context];
            var one = counts[context + 1];
            var bit = decoder.Frequency(zero + one) < zero ? 0 : 1;
            decoder.Decode(bit == 0 ? 0 : zero, bit == 0 ? zero : one);
            encoder.Encode(bit == 0 ? 0 : zero, bit == 0 ? zero : one, zero + one);
            padded[position] = (byte)bit;
            raster[y * width + x] = (byte)bit;
            counts[context + bit] += 14;
            if (counts[context] + counts[context + 1] >= 4096)
            {
                counts[context] = Math.Max(1, counts[context] >> 1);
                counts[context + 1] = Math.Max(1, counts[context + 1] >> 1);
            }
        }
        if ("!" + Pack(encoder.Finish()) != payload)
            throw new FormatException("Mask has noncanonical data or invalid finalization.");
        return raster;
    }

    private static int Context(byte[] p, int position, int stride)
    {
        var prior = position - stride;
        var earlier = prior - stride;
        return p[position - 1] | (p[position - 2] << 1) | (p[prior - 2] << 2) |
            (p[prior - 1] << 3) | (p[prior] << 4) | (p[prior + 1] << 5) | (p[prior + 2] << 6) |
            (p[earlier - 2] << 7) | (p[earlier - 1] << 8) | (p[earlier] << 9) |
            (p[earlier + 1] << 10) | (p[earlier + 2] << 11);
    }

    private static string CreateAlphabet()
    {
        var result = new StringBuilder(85);
        for (var c = '!'; c <= '~'; c++)
            if ("\"\\,;<>|".IndexOf(c) < 0) result.Append(c);
        return result.ToString();
    }

    private static int Digit(char character)
    {
        var digit = Alphabet.IndexOf(character);
        if (digit < 0 || digit >= 85) throw new FormatException("Invalid base85 digit.");
        return digit;
    }

    private static byte[] Unpack(string text, int maximumBytes)
    {
        if (text.Length < 5) throw new FormatException("Missing mask length prefix.");
        long length = 0;
        for (var i = 0; i < 5; i++) length = length * 85 + Digit(text[i]);
        var remainder = length % 4;
        var expected = 5 + length / 4 * 5 + (remainder == 0 ? 0 : remainder + 1);
        if (length > maximumBytes || text.Length != expected) throw new FormatException("Invalid encoded mask length.");
        var bytes = new byte[(int)length];
        var source = 5;
        for (var destination = 0; destination < bytes.Length; destination += 4)
        {
            var available = Math.Min(4, bytes.Length - destination);
            ulong value = 0;
            for (var i = 0; i < 5; i++)
                value = value * 85 + (ulong)(i < available + 1 ? Digit(text[source + i]) : 84);
            if (value > uint.MaxValue) throw new FormatException("Base85 group exceeds 32 bits.");
            for (var i = 0; i < available; i++) bytes[destination + i] = (byte)(value >> (24 - 8 * i));
            source += available + 1;
        }
        if (Pack(bytes) != text) throw new FormatException("Noncanonical base85 encoding.");
        return bytes;
    }

    private static string Pack(byte[] bytes)
    {
        var result = new StringBuilder();
        var prefix = new char[5];
        var length = bytes.Length;
        for (var i = 4; i >= 0; i--) { prefix[i] = Alphabet[length % 85]; length /= 85; }
        result.Append(prefix);
        for (var offset = 0; offset < bytes.Length; offset += 4)
        {
            var count = Math.Min(4, bytes.Length - offset);
            uint value = 0;
            for (var i = 0; i < 4; i++) value = (value << 8) | (uint)(i < count ? bytes[offset + i] : 0);
            var digits = new char[5];
            for (var i = 4; i >= 0; i--) { digits[i] = Alphabet[(int)(value % 85)]; value /= 85; }
            result.Append(digits, 0, count + 1);
        }
        return result.ToString();
    }

    private sealed class RangeDecoder
    {
        private readonly byte[] _bytes;
        private int _position;
        private uint _low;
        private uint _range = uint.MaxValue;
        private uint _code;
        private uint _scaled;

        internal RangeDecoder(byte[] bytes)
        {
            _bytes = bytes;
            for (var i = 0; i < 4; i++) _code = (_code << 8) | Read();
        }

        internal uint Frequency(int total)
        {
            _scaled = _range / (uint)total;
            if (_scaled == 0) throw new FormatException("Invalid range-coding state.");
            return Math.Min(unchecked(_code - _low) / _scaled, (uint)(total - 1));
        }

        internal void Decode(int cumulative, int frequency)
        {
            _low = unchecked(_low + _scaled * (uint)cumulative);
            _range = _scaled * (uint)frequency;
            while ((_low ^ unchecked(_low + _range)) < (1u << 24) || _range < (1u << 16))
            {
                if ((_low ^ unchecked(_low + _range)) >= (1u << 24)) _range = unchecked(0u - _low) & 65535;
                _code = (_code << 8) | Read();
                _low <<= 8;
                _range <<= 8;
            }
        }

        private uint Read()
        {
            if (_position >= _bytes.Length) throw new FormatException("Mask is truncated.");
            return _bytes[_position++];
        }
    }

    private sealed class RangeEncoder
    {
        private uint _low;
        private uint _range = uint.MaxValue;
        private readonly List<byte> _bytes = new List<byte>();

        internal void Encode(int cumulative, int frequency, int total)
        {
            var scaled = _range / (uint)total;
            _low = unchecked(_low + scaled * (uint)cumulative);
            _range = scaled * (uint)frequency;
            while ((_low ^ unchecked(_low + _range)) < (1u << 24) || _range < (1u << 16))
            {
                if ((_low ^ unchecked(_low + _range)) >= (1u << 24)) _range = unchecked(0u - _low) & 65535;
                _bytes.Add((byte)(_low >> 24));
                _low <<= 8;
                _range <<= 8;
            }
        }

        internal byte[] Finish()
        {
            for (var i = 0; i < 4; i++) { _bytes.Add((byte)(_low >> 24)); _low <<= 8; }
            return _bytes.ToArray();
        }
    }
}
