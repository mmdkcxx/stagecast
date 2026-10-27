using Stagecast.Core.Rendering;

namespace Stagecast.Core.Display;

/// <param name="ScaleX">Horizontal scale applied to the render surface.</param>
/// <param name="ScaleY">Vertical scale applied to the render surface.</param>
/// <param name="DestWidth">Width in monitor pixels that the image occupies (before cropping to the monitor).</param>
/// <param name="DestHeight">Height in monitor pixels that the image occupies (before cropping to the monitor).</param>
/// <param name="RestoreX">Width factor to apply downstream (Resolume/vMix) to undo the squeeze; 1 when nothing needs undoing.</param>
/// <param name="RestoreY">Height factor to apply downstream to undo the squeeze; 1 when nothing needs undoing.</param>
/// <param name="Cropped">True when part of the render is outside the monitor and not visible.</param>
public readonly record struct ScaleResult(
    double ScaleX,
    double ScaleY,
    double DestWidth,
    double DestHeight,
    double RestoreX,
    double RestoreY,
    bool Cropped);

public static class ScaleMath
{
    public static ScaleResult Compute(Resolution render, Resolution monitor, ScaleMode mode)
    {
        switch (mode)
        {
            case ScaleMode.Squeeze:
            {
                double sx = (double)monitor.Width / render.Width;
                double sy = (double)monitor.Height / render.Height;
                return new ScaleResult(sx, sy, monitor.Width, monitor.Height, 1 / sx, 1 / sy, Cropped: false);
            }
            case ScaleMode.Fit:
            {
                double s = Math.Min((double)monitor.Width / render.Width, (double)monitor.Height / render.Height);
                return new ScaleResult(s, s, render.Width * s, render.Height * s, 1, 1, Cropped: false);
            }
            case ScaleMode.OneToOne:
            {
                bool cropped = render.Width > monitor.Width || render.Height > monitor.Height;
                return new ScaleResult(1, 1, render.Width, render.Height, 1, 1, cropped);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }
}
