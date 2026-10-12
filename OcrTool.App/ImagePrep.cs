using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using OcrTool.Core;

namespace OcrTool.App;

/// <summary>
/// Screenshot polarity normalisation. Tesseract expects dark text on a light background;
/// dark-themed captures are light-on-dark and read badly (whole lines come back as
/// homoglyph garbage), so those images are inverted before OCR.
/// </summary>
internal static class ImagePrep
{
    private const int SampleSize = 96;
    private const int BucketCount = 16;
    private const int DarkBackgroundThreshold = 128;

    /// <summary>True when the dominant colour of the image is dark (light text on a dark background).</summary>
    public static bool IsLightTextOnDark(Bitmap source)
    {
        using var small = new Bitmap(SampleSize, SampleSize);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(source, 0, 0, SampleSize, SampleSize);
        }

        var buckets = new int[BucketCount];
        for (var y = 0; y < SampleSize; y++)
        {
            for (var x = 0; x < SampleSize; x++)
            {
                var c = small.GetPixel(x, y);
                var lum = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                buckets[lum >> 4]++;
            }
        }

        var best = 0;
        for (var i = 1; i < buckets.Length; i++)
        {
            if (buckets[i] > buckets[best]) best = i;
        }

        var dominant = best * 16 + 8;
        Logger.Log($"Polarity: dominant brightness {dominant} (sample {SampleSize}x{SampleSize})");
        return dominant < DarkBackgroundThreshold;
    }

    /// <summary>Returns an inverted copy for light-on-dark images, or null when the input is used as-is.</summary>
    public static Bitmap? InvertIfLightOnDark(Bitmap source)
    {
        if (!IsLightTextOnDark(source)) return null;

        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(copy))
        {
            g.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        var rect = new Rectangle(0, 0, copy.Width, copy.Height);
        var data = copy.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            if (stride < 0)
            {
                copy.UnlockBits(data);
                throw new InvalidOperationException("bottom-up bitmaps are not supported");
            }

            var buffer = new byte[stride * copy.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            for (var y = 0; y < copy.Height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < copy.Width; x++)
                {
                    var i = row + x * 4;
                    buffer[i] = (byte)(255 - buffer[i]);
                    buffer[i + 1] = (byte)(255 - buffer[i + 1]);
                    buffer[i + 2] = (byte)(255 - buffer[i + 2]);
                }
            }

            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            copy.UnlockBits(data);
        }

        Logger.Log($"Inverted light-on-dark capture: {copy.Width}x{copy.Height}");
        return copy;
    }

    /// <summary>
    /// Returns the path to OCR: the input path, or a temporary inverted copy for
    /// light-on-dark images (then <paramref name="tempPath"/> is set and must be deleted).
    /// </summary>
    public static string PrepareFile(string imagePath, out string? tempPath)
    {
        tempPath = null;
        try
        {
            using var source = new Bitmap(imagePath);
            using var inverted = InvertIfLightOnDark(source);
            if (inverted == null) return imagePath;

            var tmp = Path.Combine(Path.GetTempPath(), $"ocr_polarity_{Guid.NewGuid():N}.png");
            inverted.Save(tmp, ImageFormat.Png);
            tempPath = tmp;
            return tmp;
        }
        catch (Exception ex)
        {
            Logger.Log($"Polarity preparation skipped: {ex.Message}");
            return imagePath;
        }
    }

    public static void DeleteTemp(string? tempPath)
    {
        if (string.IsNullOrEmpty(tempPath)) return;
        try { File.Delete(tempPath!); } catch { }
    }
}
