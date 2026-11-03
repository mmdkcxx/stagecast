using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Stagecast.Core.Input;
using Stagecast.Core.Output;
using Stagecast.Core.Tabs;
using Stagecast.Rendering;

namespace Stagecast.App;

// Clicker keys and page control of the tab on air while Preview + Take shows another tab in the preview.
public partial class MainWindow
{
    private ClickerHook? _clickerHook;

    /// <summary>True while the global clicker mode is on.</summary>
    internal bool ClickerGlobal => _clickerHook is not null;

    /// <summary>The document whose pages the clicker turns: the tab on air first, otherwise the tab in the preview.</summary>
    internal IDocumentSource? ClickerTarget()
    {
        if (_engine is null) return null;
        if (_engine.Program is DocumentTab { Document.PageCount: > 0 } program) return program.Document;
        return _engine.Active is DocumentTab { Document.PageCount: > 0 } active ? active.Document : null;
    }

    /// <summary>Does what a clicker key means. Returns false when it does nothing (no document to turn).</summary>
    internal async Task<bool> RunClickerAsync(ClickerCommand command)
    {
        if (command == ClickerCommand.None || ClickerTarget() is not { } pdf) return false;
        switch (command)
        {
            case ClickerCommand.Next: await pdf.NextAsync(); break;
            case ClickerCommand.Previous: await pdf.PreviousAsync(); break;
            case ClickerCommand.Restart: await pdf.GoToAsync(FirstIndex(pdf)); break;
            case ClickerCommand.Blackout: SetGate(Gate == GateMode.Blackout ? GateMode.Live : GateMode.Blackout); break;
        }
        return true;
    }

    // Focus mode: the window's own keys. Returns true when the key was a clicker key and was used.
    private bool TryClickerKey(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var modifiers = Keyboard.Modifiers;
        ClickerCommand command = ClickerKeys.Map(virtualKey, (modifiers & ModifierKeys.Control) != 0, (modifiers & ModifierKeys.Alt) != 0, (modifiers & ModifierKeys.Shift) != 0);
        if (command == ClickerCommand.None || ClickerTarget() is null) return false;
        // Space and Enter press a focused button, and Space is TAKE in Preview + Take: those keep their meaning.
        if (key is Key.Space or Key.Enter && Keyboard.FocusedElement is ButtonBase) return false;
        if (key == Key.Space && _tabs.PreviewMode) return false;
        // A web page in the preview that has the keyboard gets its keys.
        if (_engine!.Active?.HasKeyboardFocus == true) return false;
        _ = RunClickerAsync(command);
        return true;
    }

    private void OnClickerGlobal(object sender, RoutedEventArgs e) => SetClickerGlobal(ClickerGlobalButton.IsChecked == true);

    /// <summary>Turns the global mode on or off. Returns false when it could not be turned on.</summary>
    internal bool SetClickerGlobal(bool on)
    {
        if (on == ClickerGlobal) return true;
        if (on)
        {
            try
            {
                _clickerHook = new ClickerHook((virtualKey, ctrl, alt, shift) =>
                {
                    if (!ClickerKeys.IsGlobalKey(virtualKey) || ClickerTarget() is null) return false;
                    ClickerCommand command = ClickerKeys.Map(virtualKey, ctrl, alt, shift);
                    if (command == ClickerCommand.None) return false;
                    _ = Dispatcher.BeginInvoke(() => RunClickerAsync(command));
                    return true;
                });
            }
            catch (InvalidOperationException ex)
            {
                TabError.Text = "Clicker global tidak bisa dinyalakan: " + ex.Message;
                ClickerGlobalButton.IsChecked = false;
                return false;
            }
        }
        else
        {
            _clickerHook!.Dispose();
            _clickerHook = null;
        }

        ClickerGlobalButton.IsChecked = on;
        ClickerGlobalButton.Content = on ? "CLICKER AKTIF" : "Clicker global";
        ClickerGlobalButton.Foreground = on ? (Brush)FindResource("Live") : (Brush)FindResource("Text");
        return true;
    }

    // ---- the tab on air, while the preview shows another tab ----

    internal IDocumentSource? ProgramPdf => _engine is { PreviewMode: true } engine && engine.Program is DocumentTab { Document.PageCount: > 0 } tab && tab != engine.Active ? tab.Document : null;

    internal int ProgramStripCount => _program.Items.Count;

    /// <summary>Clicks a thumbnail of the program strip, as the mouse would.</summary>
    internal void ClickProgramStrip(int index) =>
        _program.Items[index].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

    private void UpdateProgramBar()
    {
        IDocumentSource? pdf = ProgramPdf;
        ProgramDocBar.Visibility = pdf is null ? Visibility.Collapsed : Visibility.Visible;
        if (pdf is null)
        {
            ClearStrip(_program);
            ProgramStrip.Visibility = Visibility.Collapsed;
            return;
        }
        int count = pdf.PageCount;
        ProgramTitleText.Text = "PROGRAM · " + (_tabs.Items.FirstOrDefault(t => t.Id == _engine!.Program?.Id)?.Name ?? "");
        if (!ProgramPageBox.IsKeyboardFocusWithin) ProgramPageBox.Text = pdf.PageNumber.ToString();
        ProgramCountText.Text = $"/ {count}";
        ProgramPageBox.IsEnabled = count > 1;
        ProgramFirstButton.IsEnabled = ProgramPrevButton.IsEnabled = pdf.PageIndex > FirstIndex(pdf);
        ProgramNextButton.IsEnabled = ProgramLastButton.IsEnabled = pdf.PageIndex < (pdf is PowerPointSource ? LastIndex(pdf) : count - 1);

        bool strip = count > 0 && count <= MaxStripPages && _engine!.Program is DocumentTab;
        DocumentTab? programTab = _engine!.Program as DocumentTab;
        ProgramStrip.Visibility = strip ? Visibility.Visible : Visibility.Collapsed;
        if (!strip) { ClearStrip(_program); return; }
        var tab = programTab!;
        if (_program.Tab != tab.Id || _program.Pages != count) BuildStrip(_program, tab, pdf);
        UpdateStripHighlight(_program, pdf);
    }

    private void OnProgFirst(object sender, RoutedEventArgs e) => _ = GoToPageAsync(FirstIndex(ProgramPdf), ProgramPdf);

    private void OnProgPrev(object sender, RoutedEventArgs e) => _ = ProgramPdf?.PreviousAsync();

    private void OnProgNext(object sender, RoutedEventArgs e) => _ = ProgramPdf?.NextAsync();

    private void OnProgLast(object sender, RoutedEventArgs e) => _ = GoToPageAsync(LastIndex(ProgramPdf), ProgramPdf);

    private void OnProgPageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (ProgramPdf is { } pdf)
            {
                if (PageJump.TryParse(ProgramPageBox.Text, pdf.PageCount, out int index)) _ = GoToPageAsync(index, pdf); // a hidden slide asks first, as in the main bar
                else
                {
                    ProgramPageBox.Text = pdf.PageNumber.ToString();
                    TabError.Text = PageJump.Hint(pdf.PageCount);
                }
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ProgramPageBox.Text = ProgramPdf?.PageNumber.ToString() ?? "";
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnProgPageLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ProgramPdf is { } pdf) ProgramPageBox.Text = pdf.PageNumber.ToString();
    }
}
