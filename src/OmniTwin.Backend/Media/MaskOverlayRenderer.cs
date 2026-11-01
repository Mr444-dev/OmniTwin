using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using OmniTwin.Contracts;
using OmniTwin.Perception.Vision;
using OpenCvSharp;

namespace OmniTwin.Backend.Media;

internal static class MaskOverlayRenderer
{
    internal static (byte[] Png, IReadOnlyList<PipMaskMetadata> Metadata) Render(CapturedFrame frame, int width, int height,
        IReadOnlyList<MaskOverlaySample> samples, Func<int, bool>? isHazard)
    {
        using var layer = new Mat(height, width, MatType.CV_8UC4, Scalar.All(0));
        var pixels = new byte[width * height * 4]; var metadata = new List<PipMaskMetadata>(); var masks = new List<(byte[] Raster, MaskOverlaySample Sample, bool Hazard)>();
        var sx = (double)width / frame.Width; var sy = (double)height / frame.Height;
        foreach (var sample in samples)
        {
            var age = frame.CapturedAt.ToUnixTimeMilliseconds() - sample.SamCapturedAtUnixMs;
            if (sample.ImageSequenceNumber != frame.SequenceNumber || age < 0 || age >= PerceptionTiming.MaximumFrameAgeMilliseconds || sample.Quality < 0.5f) continue;
            var detection = sample.Mask; var box = detection.Box; var hazard = isHazard?.Invoke(sample.TrackId) ?? false;
            var raster = new byte[width * height];
            var left = Math.Clamp((int)Math.Floor((box.Left + sample.ShiftU) * sx), 0, width);
            var top = Math.Clamp((int)Math.Floor((box.Top + sample.ShiftV) * sy), 0, height);
            var right = Math.Clamp((int)Math.Ceiling((box.Right + 1 + sample.ShiftU) * sx), 0, width);
            var bottom = Math.Clamp((int)Math.Ceiling((box.Bottom + 1 + sample.ShiftV) * sy), 0, height);
            for (var y = top; y < bottom; y++) for (var x = left; x < right; x++)
            {
                var mx = (int)Math.Floor(((x + 0.5) / sx - box.Left - sample.ShiftU) * detection.MaskWidth / (box.Right - box.Left + 1));
                var my = (int)Math.Floor(((y + 0.5) / sy - box.Top - sample.ShiftV) * detection.MaskHeight / (box.Bottom - box.Top + 1));
                if (mx < 0 || my < 0 || mx >= detection.MaskWidth || my >= detection.MaskHeight || !detection.IsForeground(mx, my)) continue;
                raster[y * width + x] = 255; var index = (y * width + x) * 4;
                if (pixels[index + 2] == 255 && !hazard) continue;
                pixels[index] = hazard ? (byte)70 : (byte)230; pixels[index + 1] = hazard ? (byte)50 : (byte)225; pixels[index + 2] = hazard ? (byte)255 : (byte)30; pixels[index + 3] = 75;
            }
            masks.Add((raster, sample, hazard));
            metadata.Add(new PipMaskMetadata(sample.TrackId, detection.Confidence, sample.Quality, hazard, sample.ObservationSource, sample.SamSequenceNumber, sample.SamCapturedAtUnixMs));
        }
        Marshal.Copy(pixels, 0, layer.Data, pixels.Length);
        foreach (var item in masks)
        {
            using var raster = new Mat(height, width, MatType.CV_8UC1); Marshal.Copy(item.Raster, 0, raster.Data, item.Raster.Length);
            Cv2.FindContours(raster, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            var color = item.Hazard ? new Scalar(70, 50, 255, 255) : new Scalar(230, 225, 30, 255);
            foreach (var contour in contours)
            {
                if (item.Sample.ObservationSource != ObservationSource.LocalMotionEstimate) Cv2.Polylines(layer, new[] { contour }, true, color, 2);
                else for (var i = 0; i < contour.Length; i++)
                {
                    var a = contour[i]; var b = contour[(i + 1) % contour.Length]; var length = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
                    for (var start = 0.0; start < length; start += 7)
                    {
                        var end = Math.Min(length, start + 3.5);
                        Point At(double amount) => new((int)Math.Round(a.X + (b.X - a.X) * amount / length), (int)Math.Round(a.Y + (b.Y - a.Y) * amount / length));
                        Cv2.Line(layer, At(start), At(end), color);
                    }
                }
            }
            var box = item.Sample.Mask.Box;
            var position = new Point(Math.Clamp((int)((box.Left + item.Sample.ShiftU) * sx), 1, Math.Max(1, width - 90)), Math.Clamp((int)((box.Top + item.Sample.ShiftV) * sy) - 3, 10, height - 1));
            var text = "#" + item.Sample.TrackId.ToString(CultureInfo.InvariantCulture) +
                (item.Hazard ? " HAZ" : " " + item.Sample.Mask.Confidence.ToString("P0", CultureInfo.InvariantCulture));
            Cv2.PutText(layer, text, position, HersheyFonts.HersheySimplex, 0.38, new Scalar(0, 0, 0, 255), 3);
            Cv2.PutText(layer, text, position, HersheyFonts.HersheySimplex, 0.38, color, 1, LineTypes.AntiAlias);
        }
        Cv2.ImEncode(".png", layer, out var png);
        if (png.Length > MediaProtocolLimits.PreviewMaximumBytes) throw new ArgumentException("Mask layer exceeds bounds.");
        return (png, metadata.AsReadOnly());
    }
}
