using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stagecast.Core.Display;
using Stagecast.Core.Rendering;

namespace Stagecast.Spike;

/// <summary>
/// S11: squeeze 2520x1080 to 1920x1080 (width only), widen it back to 2520 like vMix/Resolume would, and measure what was lost
/// for each down-scaling filter. Horizontal-only resampling in 8-bit gamma-encoded RGB, like a GPU scaler.
/// </summary>
internal static class AnamorphicSpike
{
    private sealed record Kernel(string Name, double Radius, Func<double, double> Weight);

    private static readonly Kernel Nearest = new("nearest", 0.5, _ => 1);
    private static readonly Kernel Box = new("area (box)", 0.5, x => Math.Abs(x) <= 0.5 ? 1 : 0);
    private static readonly Kernel Bilinear = new("bilinear", 1, x => Math.Max(0, 1 - Math.Abs(x)));
    private static readonly Kernel Bicubic = new("bicubic (Catmull-Rom)", 2, x =>
    {
        x = Math.Abs(x);
        return x < 1 ? 1.5 * x * x * x - 2.5 * x * x + 1 : x < 2 ? -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2 : 0;
    });
    private static readonly Kernel Lanczos3 = new("lanczos3", 3, x =>
    {
        x = Math.Abs(x);
        return x < 1e-9 ? 1 : x < 3 ? Sinc(x) * Sinc(x / 3) : 0;
    });

    private static double Sinc(double x) => Math.Sin(Math.PI * x) / (Math.PI * x);

    public static void Run(string sourcePng, string regionsJson, string outDir)
    {
        var render = new Resolution(2520, 1080);
        var monitor = new Resolution(1920, 1080);
        var scale = ScaleMath.Compute(render, monitor, ScaleMode.Squeeze);
        SpikeLog.Write($"S11 factors from Core: squeeze width {scale.ScaleX * 100:F2}% height {scale.ScaleY * 100:F2}%, restore width {scale.RestoreX * 100:F2}% height {scale.RestoreY * 100:F2}%");

        var (src, w, h) = LoadBgra(sourcePng);
        var regions = JsonSerializer.Deserialize<Dictionary<string, int[]>>(regionsJson)!;
        Kernel[] downs = [Nearest, Box, Bilinear, Bicubic, Lanczos3];
        Kernel[] restores = [Bilinear, Bicubic];

        var sheetRows = new List<(string Label, byte[] Pixels)> { ("source 2520x1080", src) };
        foreach (var restore in restores)
        {
            SpikeLog.Write($"S11 --- widened back with {restore.Name} ---");
            SpikeLog.Write("S11 filter                  PSNR all  PSNR text  1px lines  1:2 lines  2px lines  edge 10-90%");
            foreach (var down in downs)
            {
                var squeezed = Resample(src, w, h, monitor.Width, down);
                var back = Resample(squeezed, monitor.Width, h, w, restore);
                double psnrAll = Psnr(src, back, w, new[] { 0, 0, w, h });
                double psnrText = Psnr(src, back, w, regions["text"]);
                double l2 = Contrast(back, w, regions["lines2px"]), l3 = Contrast(back, w, regions["lines3px"]), l4 = Contrast(back, w, regions["lines4px"]);
                double edge = EdgeWidth(back, w, regions["edge"]);
                SpikeLog.Write($"S11 {down.Name,-24} {psnrAll,8:F1} {psnrText,10:F1} {l2,10:P0} {l3,10:P0} {l4,10:P0} {edge,12:F2} px");
                if (restore == Bilinear) sheetRows.Add(($"{down.Name} -> bilinear back", back));
                if (restore == Bilinear && down == Box) SavePng(Path.Combine(outDir, "s11-squeezed-1920-area.png"), squeezed, monitor.Width, h);
            }
        }
        WriteSheet(Path.Combine(outDir, "s11-compare.png"), sheetRows, w, regions);
        SpikeLog.Write($"S11 images saved in {outDir}: s11-compare.png (crops of text and lines, 2x), s11-squeezed-1920-area.png (what the HDMI output carries)");
    }

    // 1-D horizontal resample. When shrinking, the kernel is stretched by 1/scale so it averages the source (anti-aliasing).
    private static byte[] Resample(byte[] src, int w, int h, int newW, Kernel k)
    {
        double scale = (double)newW / w;
        double stretch = Math.Max(1, 1 / scale);
        var dst = new byte[newW * h * 4];
        var taps = new (int Start, float[] Weights)[newW];
        for (int x = 0; x < newW; x++)
        {
            double center = (x + 0.5) / scale - 0.5;
            if (ReferenceEquals(k, Nearest))
            {
                taps[x] = ((int)Math.Clamp(Math.Floor(center + 0.5), 0, w - 1), [1f]);
                continue;
            }
            int first = (int)Math.Ceiling(center - k.Radius * stretch), last = (int)Math.Floor(center + k.Radius * stretch);
            var weights = new float[last - first + 1];
            double sum = 0;
            for (int i = first; i <= last; i++) sum += weights[i - first] = (float)k.Weight((i - center) / stretch);
            for (int i = 0; i < weights.Length; i++) weights[i] = (float)(weights[i] / sum);
            taps[x] = (first, weights);
        }
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < newW; x++)
            {
                var (start, weights) = taps[x];
                float b = 0, g = 0, r = 0;
                for (int i = 0; i < weights.Length; i++)
                {
                    int sx = Math.Clamp(start + i, 0, w - 1) * 4 + row;
                    b += src[sx] * weights[i]; g += src[sx + 1] * weights[i]; r += src[sx + 2] * weights[i];
                }
                int o = (y * newW + x) * 4;
                dst[o] = (byte)Math.Clamp(b + 0.5f, 0, 255); dst[o + 1] = (byte)Math.Clamp(g + 0.5f, 0, 255);
                dst[o + 2] = (byte)Math.Clamp(r + 0.5f, 0, 255); dst[o + 3] = 255;
            }
        }
        return dst;
    }

    private static double Luma(byte[] p, int i) => 0.0722 * p[i] + 0.7152 * p[i + 1] + 0.2126 * p[i + 2];

    private static double Psnr(byte[] a, byte[] b, int w, int[] rect)
    {
        double sum = 0; long n = 0;
        for (int y = rect[1]; y < rect[1] + rect[3]; y++)
            for (int x = rect[0]; x < rect[0] + rect[2]; x++)
            {
                double d = Luma(a, (y * w + x) * 4) - Luma(b, (y * w + x) * 4);
                sum += d * d; n++;
            }
        double mse = sum / n;
        return mse < 1e-9 ? 99 : 10 * Math.Log10(255 * 255 / mse);
    }

    // Peak-to-valley of the column-averaged luma, in the middle of the region: 100% = full black/white lines survive.
    private static double Contrast(byte[] p, int w, int[] rect)
    {
        double min = 255, max = 0;
        for (int x = rect[0] + 20; x < rect[0] + rect[2] - 20; x++)
        {
            double sum = 0;
            for (int y = rect[1] + 20; y < rect[1] + rect[3] - 20; y++) sum += Luma(p, (y * w + x) * 4);
            double mean = sum / (rect[3] - 40);
            min = Math.Min(min, mean); max = Math.Max(max, mean);
        }
        return (max - min) / 255;
    }

    // Distance in pixels over which a black-to-white edge goes from 10% to 90%. A perfect edge is about 1 px or less.
    private static double EdgeWidth(byte[] p, int w, int[] rect)
    {
        int mid = rect[0] + rect[2] / 2;
        double? t10 = null, t90 = null;
        double prev = ColumnMean(p, w, rect, mid - 12);
        for (int x = mid - 11; x <= mid + 12; x++)
        {
            double v = ColumnMean(p, w, rect, x);
            if (t10 is null && prev < 25.5 && v >= 25.5) t10 = x - 1 + (25.5 - prev) / (v - prev);
            if (t90 is null && prev < 229.5 && v >= 229.5) t90 = x - 1 + (229.5 - prev) / (v - prev);
            prev = v;
        }
        return t10 is null || t90 is null ? double.NaN : t90.Value - t10.Value;
    }

    private static double ColumnMean(byte[] p, int w, int[] rect, int x)
    {
        double sum = 0;
        for (int y = rect[1] + 20; y < rect[1] + rect[3] - 20; y++) sum += Luma(p, (y * w + x) * 4);
        return sum / (rect[3] - 40);
    }

    private static void WriteSheet(string path, List<(string Label, byte[] Pixels)> rows, int w, Dictionary<string, int[]> regions)
    {
        // Left: the 10-14 px text. Right: 1px lines, 1:2 lines and the edge. Every crop is enlarged 2x without smoothing.
        int[] textCrop = [regions["text"][0], regions["text"][1], 480, 110];
        int[] lineCrop = [regions["lines2px"][0] + 60, regions["lines2px"][1] + 20, 480, 110];
        int zoom = 2, cellW = 480 * zoom, cellH = 110 * zoom, labelH = 20, gap = 6;
        int sheetW = cellW * 2 + gap, sheetH = rows.Count * (cellH + labelH + gap);
        var sheet = new byte[sheetW * sheetH * 4];
        Array.Fill(sheet, (byte)40);
        for (int r = 0; r < rows.Count; r++)
        {
            int top = r * (cellH + labelH + gap) + labelH;
            for (int c = 0; c < 2; c++)
            {
                var crop = c == 0 ? textCrop : lineCrop;
                for (int y = 0; y < cellH; y++)
                    for (int x = 0; x < cellW; x++)
                    {
                        int s = ((crop[1] + y / zoom) * w + crop[0] + x / zoom) * 4;
                        int d = ((top + y) * sheetW + c * (cellW + gap) + x) * 4;
                        Array.Copy(rows[r].Pixels, s, sheet, d, 4);
                    }
            }
        }
        // Row labels are drawn with WPF text onto the finished bitmap.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(BitmapSource.Create(sheetW, sheetH, 96, 96, PixelFormats.Bgra32, null, sheet, sheetW * 4), new System.Windows.Rect(0, 0, sheetW, sheetH));
            for (int r = 0; r < rows.Count; r++)
            {
                var text = new FormattedText(rows[r].Label, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    new Typeface("Consolas"), 14, Brushes.White, 1.0);
                dc.DrawText(text, new System.Windows.Point(4, r * (cellH + labelH + gap) + 2));
            }
        }
        var target = new RenderTargetBitmap(sheetW, sheetH, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        using var file = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(target));
        enc.Save(file);
    }

    private static (byte[] Pixels, int W, int H) LoadBgra(string png)
    {
        using var stream = File.OpenRead(png);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return (pixels, converted.PixelWidth, converted.PixelHeight);
    }

    private static void SavePng(string path, byte[] bgra, int w, int h)
    {
        using var file = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4)));
        enc.Save(file);
    }
}
