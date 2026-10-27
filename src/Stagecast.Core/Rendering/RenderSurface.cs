namespace Stagecast.Core.Rendering;

public static class RenderSurface
{
    /// <summary>
    /// Physical pixel size the hosted browser needs so that the page sees <paramref name="css"/> as its viewport
    /// at the given rasterization scale (Windows display scale, e.g. 1.5 for 150%).
    /// </summary>
    public static (int Width, int Height) PhysicalSize(Resolution css, double scale)
    {
        if (!(scale > 0) || double.IsInfinity(scale))
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "scale must be a positive finite number");

        return ((int)Math.Round(css.Width * scale, MidpointRounding.AwayFromZero),
                (int)Math.Round(css.Height * scale, MidpointRounding.AwayFromZero));
    }
}
