using System.Windows;
using System.Windows.Media;

namespace Stagecast.App;

/// <summary>A window that only holds the page preview, for moving it to another monitor.</summary>
public sealed class PreviewWindow : Window
{
    public PreviewWindow(PagePreview preview)
    {
        Title = "Stagecast - Preview";
        Width = 1100;
        Height = 560;
        Background = Brushes.Black;
        Content = preview;
    }
}
