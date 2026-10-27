using Stagecast.Core.Rendering;

namespace Stagecast.Core.Display;

/// <summary>How the operator's preview shows the rendered page: fitted to the control, or at 100% around a point.</summary>
public enum ViewMode
{
    Fit,
    Actual,
}

/// <summary>
/// Maps between a point in the preview control (device-independent pixels) and a point in the page viewport (CSS pixels).
/// The preview only changes how the page is drawn for the operator; the rendered output is never affected.
/// </summary>
public readonly record struct ViewTransform(double Scale, double OffsetX, double OffsetY)
{
    /// <param name="render">Size of the rendered page.</param>
    /// <param name="controlWidth">Width of the preview control.</param>
    /// <param name="controlHeight">Height of the preview control.</param>
    /// <param name="mode">Fit shows everything; Actual shows 1:1 around <paramref name="centerX"/>, <paramref name="centerY"/>.</param>
    /// <param name="centerX">Page x shown at the centre of the control in Actual mode.</param>
    /// <param name="centerY">Page y shown at the centre of the control in Actual mode.</param>
    public static ViewTransform Create(Resolution render, double controlWidth, double controlHeight, ViewMode mode, double centerX = 0, double centerY = 0)
    {
        if (controlWidth <= 0 || controlHeight <= 0) throw new ArgumentOutOfRangeException(nameof(controlWidth), "control must have a size");

        if (mode == ViewMode.Fit)
        {
            double fit = Math.Min(controlWidth / render.Width, controlHeight / render.Height);
            return new ViewTransform(fit, (controlWidth - render.Width * fit) / 2, (controlHeight - render.Height * fit) / 2);
        }

        return new ViewTransform(1.0, ClampOffset(controlWidth / 2 - centerX, render.Width, controlWidth), ClampOffset(controlHeight / 2 - centerY, render.Height, controlHeight));
    }

    // The page may not be dragged away from the control: it stays covering the control, or centred when it is smaller.
    private static double ClampOffset(double offset, int renderSize, double controlSize)
    {
        if (renderSize <= controlSize) return (controlSize - renderSize) / 2;
        return Math.Clamp(offset, controlSize - renderSize, 0);
    }

    /// <summary>The page point under a control point. Not clamped: use <see cref="Contains"/> to tell if it is on the page.</summary>
    public (double X, double Y) ToPage(double controlX, double controlY) => ((controlX - OffsetX) / Scale, (controlY - OffsetY) / Scale);

    /// <summary>Where a page point is drawn in the control.</summary>
    public (double X, double Y) ToControl(double pageX, double pageY) => (pageX * Scale + OffsetX, pageY * Scale + OffsetY);

    public bool Contains(Resolution render, double controlX, double controlY)
    {
        var (x, y) = ToPage(controlX, controlY);
        return x >= 0 && y >= 0 && x < render.Width && y < render.Height;
    }
}
