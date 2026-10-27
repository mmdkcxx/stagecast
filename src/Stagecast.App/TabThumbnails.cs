using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stagecast.Rendering.Capture;

namespace Stagecast.App;

/// <summary>
/// Small pictures of the tabs for the tab list. A tab only has a picture to give while it is on the stage, so each one is the last
/// look the operator or the output had of it, refreshed every <see cref="IntervalMs"/> ms while it is shown.
/// </summary>
internal sealed class TabThumbnails
{
    public const int MaxWidth = 192;
    public const int IntervalMs = 1500;

    private readonly Dictionary<Guid, BitmapSource> _images = [];
    private readonly Dictionary<Guid, long> _taken = [];
    private readonly object _gate = new();

    public BitmapSource? Get(Guid id) => _images.GetValueOrDefault(id);

    public void Set(Guid id, BitmapSource image) => _images[id] = image;

    public void Keep(IEnumerable<Guid> ids)
    {
        var keep = ids.ToHashSet();
        foreach (var id in _images.Keys.Where(id => !keep.Contains(id)).ToList()) _images.Remove(id);
        lock (_gate)
            foreach (var id in _taken.Keys.Where(id => !keep.Contains(id)).ToList()) _taken.Remove(id);
    }

    /// <summary>True when <paramref name="id"/> is due a new picture; marks it as taken. Callable from any thread.</summary>
    public bool Due(Guid id, long nowMs)
    {
        lock (_gate)
        {
            if (_taken.TryGetValue(id, out var last) && nowMs - last < IntervalMs) return false;
            _taken[id] = nowMs;
            return true;
        }
    }

    /// <summary>Shrinks a preview picture (BGRA) to at most <see cref="MaxWidth"/> pixels wide by averaging blocks. Callable from any thread.</summary>
    public static BitmapSource? Downscale(PreviewImage image)
    {
        if (image.Width < 8 || image.Height < 8 || image.Pixels.Length < image.Stride * (image.Height - 1) + image.Width * 4) return null;
        int step = Math.Max(1, (int)Math.Ceiling(image.Width / (double)MaxWidth));
        int width = image.Width / step, height = image.Height / step;
        var output = new byte[width * height * 4];
        int block = step * step;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int b = 0, g = 0, r = 0;
                for (int dy = 0; dy < step; dy++)
                {
                    int row = (y * step + dy) * image.Stride + x * step * 4;
                    for (int dx = 0; dx < step; dx++)
                    {
                        int at = row + dx * 4;
                        b += image.Pixels[at];
                        g += image.Pixels[at + 1];
                        r += image.Pixels[at + 2];
                    }
                }
                int o = (y * width + x) * 4;
                output[o] = (byte)(b / block);
                output[o + 1] = (byte)(g / block);
                output[o + 2] = (byte)(r / block);
                output[o + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, output, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
