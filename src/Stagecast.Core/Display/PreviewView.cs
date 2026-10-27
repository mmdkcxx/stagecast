using Stagecast.Core.Rendering;

namespace Stagecast.Core.Display;

/// <summary>
/// What the operator's preview currently shows: the whole page fitted to the control, or the page at 100% around a centre point
/// that can be dragged. All sizes are physical pixels of the control. Only the preview is affected, never the rendered output.
/// </summary>
public sealed class PreviewView
{
    public ViewMode Mode { get; private set; } = ViewMode.Fit;

    /// <summary>Page x shown at the centre of the control in <see cref="ViewMode.Actual"/>.</summary>
    public double CenterX { get; private set; }

    public double CenterY { get; private set; }

    public void ShowFit() => Mode = ViewMode.Fit;

    /// <summary>Switches to 100%, keeping the page point now at the centre of the control, or the page centre when not known.</summary>
    public void ShowActual(Resolution render, double controlWidth, double controlHeight)
    {
        if (Mode == ViewMode.Fit)
        {
            CenterX = render.Width / 2.0;
            CenterY = render.Height / 2.0;
        }
        Mode = ViewMode.Actual;
        Clamp(render, controlWidth, controlHeight);
    }

    /// <summary>Shows 100% around a page point, for example when the mini-map is clicked.</summary>
    public void ShowActualAt(Resolution render, double controlWidth, double controlHeight, double pageX, double pageY)
    {
        Mode = ViewMode.Actual;
        CenterX = pageX;
        CenterY = pageY;
        Clamp(render, controlWidth, controlHeight);
    }

    /// <summary>Drags the page by a distance in control pixels (the page follows the pointer). Only moves in 100% mode.</summary>
    public void PanBy(Resolution render, double controlWidth, double controlHeight, double dx, double dy)
    {
        if (Mode != ViewMode.Actual) return;
        CenterX -= dx;
        CenterY -= dy;
        Clamp(render, controlWidth, controlHeight);
    }

    public ViewTransform Transform(Resolution render, double controlWidth, double controlHeight)
        => ViewTransform.Create(render, controlWidth, controlHeight, Mode, CenterX, CenterY);

    /// <summary>True when 100% does not fit, so some of the page is out of sight and can be dragged into view.</summary>
    public bool IsCropped(Resolution render, double controlWidth, double controlHeight)
        => Mode == ViewMode.Actual && (render.Width > controlWidth || render.Height > controlHeight);

    /// <summary>The page rectangle that is visible in the control (page pixels), for the mini-map.</summary>
    public (double X, double Y, double Width, double Height) Visible(Resolution render, double controlWidth, double controlHeight)
    {
        var t = Transform(render, controlWidth, controlHeight);
        double x0 = Math.Max(0, -t.OffsetX / t.Scale), y0 = Math.Max(0, -t.OffsetY / t.Scale);
        double x1 = Math.Min(render.Width, (controlWidth - t.OffsetX) / t.Scale), y1 = Math.Min(render.Height, (controlHeight - t.OffsetY) / t.Scale);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>What has to be read back from the rendered page to draw the preview without upscaling.</summary>
    public PreviewRegion Region(Resolution render, double controlWidth, double controlHeight)
    {
        if (Mode == ViewMode.Fit)
        {
            double fit = Transform(render, controlWidth, controlHeight).Scale;
            int mip = fit >= 1 ? 0 : (int)Math.Floor(Math.Log2(1 / fit));
            return new PreviewRegion(0, 0, render.Width, render.Height, Math.Clamp(mip, 0, PreviewRegion.MaxMip));
        }
        var (x, y, w, h) = Visible(render, controlWidth, controlHeight);
        int rx = (int)Math.Floor(x), ry = (int)Math.Floor(y);
        return new PreviewRegion(rx, ry, Math.Min(render.Width - rx, (int)Math.Ceiling(w + (x - rx))), Math.Min(render.Height - ry, (int)Math.Ceiling(h + (y - ry))), 0);
    }

    // Keeps the stored centre where the transform actually puts it, so dragging past the edge does not build up a hidden distance.
    private void Clamp(Resolution render, double controlWidth, double controlHeight)
    {
        var t = ViewTransform.Create(render, controlWidth, controlHeight, ViewMode.Actual, CenterX, CenterY);
        CenterX = render.Width <= controlWidth ? render.Width / 2.0 : (controlWidth / 2 - t.OffsetX) / t.Scale;
        CenterY = render.Height <= controlHeight ? render.Height / 2.0 : (controlHeight / 2 - t.OffsetY) / t.Scale;
    }
}
