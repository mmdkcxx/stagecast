using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Stagecast.Rendering;

namespace Stagecast.App;

// The page strip under the preview can be made taller by dragging its grip upwards: bigger thumbnails make it possible to read the next
// slide. The smallest size is the original one. The strip in the PROGRAM bar keeps the small size.
public partial class MainWindow
{
    private const double MaxStripThumbHeight = 240;
    private const double MinPreviewHeight = 200;

    private double _stripThumbHeight = StripThumbHeight;

    // The width the thumbnails of the main strip are drawn at: only grows, so a bigger strip made smaller again keeps its sharp pictures.
    private int _stripRenderWidth = StripThumbWidth * 2;

    private double _gripStartY;
    private double _gripStartHeight;
    private bool _gripDragging;

    private (double Width, double Height) ThumbSize(StripState st)
        => ReferenceEquals(st, _main) ? (_stripThumbHeight * 16 / 9, _stripThumbHeight) : (StripThumbWidth, StripThumbHeight);

    private (int Width, int Height) ThumbRenderSize(StripState st)
    {
        int w = ReferenceEquals(st, _main) ? _stripRenderWidth : StripThumbWidth * 2;
        return (w, w * 9 / 16);
    }

    // Big thumbnails use more memory each, so fewer of them are kept around the page on screen.
    private int StripWindowFor(StripState st) => ReferenceEquals(st, _main) && _stripThumbHeight > StripThumbHeight * 1.4 ? 60 : StripWindow;

    private void OnStripGripDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            SetStripHeight(StripThumbHeight, final: true);
            e.Handled = true;
            return;
        }
        _gripStartY = e.GetPosition(this).Y; // relative to the window: the grip itself moves while the strip grows
        _gripStartHeight = _stripThumbHeight;
        _gripDragging = StripGrip.CaptureMouse();
        e.Handled = true;
    }

    private void OnStripGripMove(object sender, MouseEventArgs e)
    {
        if (!_gripDragging) return;
        SetStripHeight(_gripStartHeight + (_gripStartY - e.GetPosition(this).Y), final: false);
    }

    private void OnStripGripUp(object sender, MouseButtonEventArgs e)
    {
        if (!_gripDragging) return;
        _gripDragging = false;
        StripGrip.ReleaseMouseCapture();
        SetStripHeight(_stripThumbHeight, final: true);
    }

    private void OnStripGripLost(object sender, MouseEventArgs e)
    {
        if (!_gripDragging) return;
        _gripDragging = false;
        SetStripHeight(_stripThumbHeight, final: true);
    }

    /// <summary>Sets the thumbnail height of the main strip (clamped; the preview keeps a minimum height). When the drag ends, bigger sizes get sharper pictures.</summary>
    private void SetStripHeight(double height, bool final)
    {
        double room = _stripThumbHeight + Math.Max(0, PreviewHost.ActualHeight - MinPreviewHeight);
        height = Math.Clamp(height, StripThumbHeight, Math.Max(StripThumbHeight, Math.Min(MaxStripThumbHeight, room)));
        if (Math.Abs(height - _stripThumbHeight) >= 0.5)
        {
            _stripThumbHeight = height;
            foreach (var item in _main.Items)
            {
                if (item.Child is Grid grid)
                {
                    grid.Width = height * 16 / 9;
                    grid.Height = height;
                }
            }
        }
        if (final) RefreshStripPictures();
    }

    private void RefreshStripPictures()
    {
        double scale = _stripThumbHeight <= StripThumbHeight ? 2 : 1.5;
        int wanted = Math.Max(StripThumbWidth * 2, (int)Math.Ceiling(_stripThumbHeight * 16 / 9 * scale / 32) * 32);
        if (wanted <= _stripRenderWidth || _main.Tab is not { } id) return;
        _stripRenderWidth = wanted;
        // The pictures drawn so far stay on screen until the sharper ones replace them, page on screen first.
        foreach (var known in _pageThumbs.Values) known.Clear();
        if (_engine?.TabById(id) is DocumentTab { Document: { } pdf })
        {
            int center = _main.Items.Count > 0 ? Math.Clamp(pdf.PageIndex, 0, _main.Items.Count - 1) : 0;
            _ = LoadStripAsync(_main, id, pdf, ++_main.Version, center);
        }
    }
}
