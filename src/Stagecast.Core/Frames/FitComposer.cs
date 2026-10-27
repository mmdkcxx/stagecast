namespace Stagecast.Core.Frames;

/// <summary>Where a picture goes on the canvas: <see cref="Width"/> x <see cref="Height"/> at (<see cref="X"/>, <see cref="Y"/>).</summary>
public readonly record struct FitLayout(int X, int Y, int Width, int Height, int CanvasWidth, int CanvasHeight)
{
    /// <summary>Width of the bar on the left side (the right bar is the same, give or take a pixel).</summary>
    public int BarLeft => X;

    public int BarRight => CanvasWidth - X - Width;

    public int BarTop => Y;

    public int BarBottom => CanvasHeight - Y - Height;

    public bool HasBars => Width < CanvasWidth || Height < CanvasHeight;
}

/// <summary>
/// Scale to fit: the whole picture is shown at the largest size that fits the canvas, centred, aspect ratio kept. Nothing is cropped
/// and the source file is never changed; the space left over is bars.
/// </summary>
public static class FitComposer
{
    public static FitLayout Fit(double sourceWidth, double sourceHeight, int canvasWidth, int canvasHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0) throw new ArgumentOutOfRangeException(nameof(canvasWidth), "The canvas must have a size.");
        if (!(sourceWidth > 0) || !(sourceHeight > 0) || double.IsInfinity(sourceWidth) || double.IsInfinity(sourceHeight))
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "The picture must have a size.");

        double scale = Math.Min(canvasWidth / sourceWidth, canvasHeight / sourceHeight);
        int width = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, canvasWidth);
        int height = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, canvasHeight);
        return new FitLayout((canvasWidth - width) / 2, (canvasHeight - height) / 2, width, height, canvasWidth, canvasHeight);
    }
}
