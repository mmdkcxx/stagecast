using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Stagecast.App;

/// <summary>Asks for the password of a protected document. The text lives only as long as this call; nothing keeps it.</summary>
internal static class PasswordDialog
{
    /// <summary>The password the operator typed, or null when they cancelled.</summary>
    public static string? Ask(Window owner, string fileName, bool wrongBefore)
    {
        var box = new PasswordBox { MinHeight = 34, FontSize = 14, Margin = new Thickness(0, 10, 0, 0) };
        var message = new TextBlock
        {
            Text = (wrongBefore ? "Kata sandi salah. " : "") + $"“{fileName}” diberi kata sandi. Kata sandi tidak disimpan oleh Stagecast.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 380, FontSize = 14,
        };
        var ok = new Button { Content = "Buka", IsDefault = true, MinWidth = 84, MinHeight = 32, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Batal", IsCancel = true, MinWidth = 84, MinHeight = 32 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(message);
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = "Stagecast", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Content = panel,
        };
        if (Application.Current.TryFindResource("Panel") is Brush background) window.Background = background;
        if (Application.Current.TryFindResource("Text") is Brush foreground) message.Foreground = foreground;
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true ? box.Password : null;
    }
}
