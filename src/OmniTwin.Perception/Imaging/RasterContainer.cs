using System;
using System.IO;

namespace OmniTwin.Perception.Imaging;

/// <summary>Validated encoded raster dimensions, before any native image allocation.</summary>
public readonly struct RasterInfo
{
    internal RasterInfo(int width, int height, bool png) { Width = width; Height = height; IsPng = png; }
    /// <summary>Gets raster columns.</summary>
    public int Width { get; }
    /// <summary>Gets raster rows.</summary>
    public int Height { get; }
    /// <summary>Gets whether this is PNG rather than baseline JPEG.</summary>
    public bool IsPng { get; }
}

/// <summary>Bounded single-image PNG/baseline JPEG validation. Rejects animation, extra frames, truncated containers and oversized rasters.</summary>
public static class RasterContainer
{
    private static readonly uint[] CrcTable = MakeTable();
    /// <summary>Checks dimensions, PNG chunk CRCs/order, and JPEG frame/scan markers before a native decoder is used.</summary>
    public static RasterInfo Inspect(ReadOnlySpan<byte> bytes, int maximumSide = 2048, int maximumBytes = 8 * 1024 * 1024)
    {
        if (maximumSide < 1 || maximumSide > 4096 || bytes.Length < 4 || bytes.Length > maximumBytes) throw new InvalidDataException("image_size");
        if (bytes.Length >= 33 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71)
        {
            if (bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10) throw new InvalidDataException("png_signature");
            var offset = 8; var header = false; var data = false; var dataEnded = false; var chunks = 0; var width = 0; var height = 0;
            while (offset + 12 <= bytes.Length)
            {
                if (++chunks > 4096) throw new InvalidDataException("png_chunk_limit");
                var length = U32(bytes, offset); if (length > (uint)(bytes.Length - offset - 12)) throw new InvalidDataException("png_chunk_size");
                var count = (int)length; var type = U32(bytes, offset + 4);
                var crc = uint.MaxValue;
                for (var i = offset + 4; i < offset + 8 + count; i++) crc = CrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8);
                if (~crc != U32(bytes, offset + 8 + count)) throw new InvalidDataException("png_crc");
                if (!header)
                {
                    if (type != 0x49484452 || count != 13) throw new InvalidDataException("png_header");
                    width = Dimension(U32(bytes, offset + 8), maximumSide); height = Dimension(U32(bytes, offset + 12), maximumSide);
                    if (bytes[offset + 16] != 8 || (bytes[offset + 17] != 2 && bytes[offset + 17] != 6) || bytes[offset + 18] != 0 || bytes[offset + 19] != 0 || bytes[offset + 20] != 0)
                        throw new InvalidDataException("png_format");
                    header = true;
                }
                else if (type == 0x49484452 || type == 0x6163544c || type == 0x6663544c || type == 0x66644154) throw new InvalidDataException("multiple_image_frames");
                else if (type == 0x49444154) { if (dataEnded || count == 0) throw new InvalidDataException("png_data_order"); data = true; }
                else if (type == 0x49454e44)
                { if (!data || count != 0 || offset + 12 != bytes.Length) throw new InvalidDataException("png_end"); return new RasterInfo(width, height, true); }
                else { if (data) dataEnded = true; if ((bytes[offset + 4] & 32) == 0 && type != 0x504c5445) throw new InvalidDataException("png_critical_chunk"); }
                offset += count + 12;
            }
            throw new InvalidDataException("png_truncated");
        }
        if (bytes[0] != 255 || bytes[1] != 216) throw new InvalidDataException("image_format");
        var p = 2; var w = 0; var h = 0; var scan = -1;
        while (p + 4 <= bytes.Length)
        {
            if (bytes[p++] != 255) throw new InvalidDataException("jpeg_marker");
            while (p < bytes.Length && bytes[p] == 255) p++;
            if (p >= bytes.Length) break; var marker = bytes[p++];
            if (marker == 1 || (marker >= 208 && marker <= 215)) continue;
            if (p + 2 > bytes.Length) break; var size = (bytes[p] << 8) | bytes[p + 1];
            if (size < 2 || p + size > bytes.Length) throw new InvalidDataException("jpeg_segment");
            if (marker == 218) { if (w == 0) throw new InvalidDataException("jpeg_missing_frame"); scan = p + size; break; }
            if (marker >= 192 && marker <= 207 && marker != 196 && marker != 200 && marker != 204)
            {
                if (w != 0 || marker != 192 || size < 8 || bytes[p + 2] != 8) throw new InvalidDataException("jpeg_frame");
                h = Dimension((uint)((bytes[p + 3] << 8) | bytes[p + 4]), maximumSide); w = Dimension((uint)((bytes[p + 5] << 8) | bytes[p + 6]), maximumSide);
            }
            p += size;
        }
        if (scan < 0) throw new InvalidDataException("jpeg_scan");
        for (var i = scan; i < bytes.Length - 1; i++)
        {
            if (bytes[i] != 255) continue;
            while (i + 1 < bytes.Length && bytes[i + 1] == 255) i++;
            if (++i >= bytes.Length) break; var marker = bytes[i];
            if (marker == 0 || (marker >= 208 && marker <= 215)) continue;
            if (marker == 217 && i == bytes.Length - 1) return new RasterInfo(w, h, false);
            throw new InvalidDataException("jpeg_extra_frame_or_scan");
        }
        throw new InvalidDataException("jpeg_truncated");
    }
    private static int Dimension(uint value, int maximum) => value > 0 && value <= maximum ? (int)value : throw new InvalidDataException("image_dimensions");
    private static uint U32(ReadOnlySpan<byte> bytes, int p) => ((uint)bytes[p] << 24) | ((uint)bytes[p + 1] << 16) | ((uint)bytes[p + 2] << 8) | bytes[p + 3];
    private static uint[] MakeTable()
    {
        var values = new uint[256];
        for (uint i = 0; i < values.Length; i++) { var value = i; for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1; values[i] = value; }
        return values;
    }
}
