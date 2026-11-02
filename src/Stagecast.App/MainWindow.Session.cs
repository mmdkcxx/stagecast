using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Stagecast.Core.Configuration;
using Stagecast.Core.Output;
using Stagecast.Core.Rendering;
using Stagecast.Core.Sessions;
using Stagecast.Core.Tabs;
using Stagecast.Rendering;

namespace Stagecast.App;

internal enum SaveAnswer
{
    Save,
    Discard,
    Cancel,
}

// Sessions: the tabs of a show saved to a .stagecast file and opened again, here or on another computer. The application itself always
// starts empty. A session holds addresses, document paths, the mode and the render size; never a login (that stays in the browser profile).
public partial class MainWindow
{
    private string? _sessionPath;
    private bool _sessionDirty;
    private bool _applyingSession;
    private List<string> _recentSessions = [];

    // Tabs whose document was not found when a session was opened. The banner stays until each has a file or is closed.
    private readonly HashSet<Guid> _missingTabs = [];

    internal string? SessionPath => _sessionPath;

    internal bool SessionDirty => _sessionDirty;

    internal IReadOnlyList<string> RecentSessions => _recentSessions;

    internal int MissingDocumentCount => _missingTabs.Count(id => _tabs.IndexOf(id) >= 0);

    // The questions and file pickers; replaceable so the tests need no dialog. Null means the real dialog.
    internal Func<string?>? PickSessionToOpen { get; set; }

    internal Func<string?>? PickSessionToSave { get; set; }

    internal Func<PlannedTab, string?>? PickMissingFile { get; set; }

    internal Func<string?>? PickFolder { get; set; }

    /// <summary>Session name, tabs now, tabs in the session, resolution offer (null when it needs no question), a tab is on air, can save first, save first by default.</summary>
    internal Func<string, int, int, string?, bool, bool, bool, OpenChoice?>? AskOpenChoice { get; set; }

    internal Func<string, SessionPlan, ISessionFiles, SessionPlan?>? AskRelink { get; set; }

    internal Func<string, SaveAnswer>? AskSaveChanges { get; set; }

    internal Action<string>? Notify { get; set; }

    private void Tell(string text)
    {
        if (Notify is not null) Notify(text);
        else MessageBox.Show(this, text, "Stagecast", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MarkSessionDirty()
    {
        if (_applyingSession || _sessionDirty) return;
        _sessionDirty = true;
        RefreshSessionChip();
    }

    private void RefreshSessionChip()
    {
        if (_sessionPath is null)
        {
            SessionNameText.Text = "Sesi baru";
            SessionButton.ToolTip = "Sesi: simpan dan buka tab (Ctrl+S, Ctrl+O)";
            return;
        }
        SessionNameText.Text = Path.GetFileNameWithoutExtension(_sessionPath) + (_sessionDirty ? " •" : "");
        SessionButton.ToolTip = _sessionPath + (_sessionDirty ? "\nAda perubahan yang belum disimpan." : "");
    }

    private void RememberSession(string path)
    {
        _recentSessions.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _recentSessions.Insert(0, path);
        if (_recentSessions.Count > AppConfig.MaxRecentSessions) _recentSessions.RemoveRange(AppConfig.MaxRecentSessions, _recentSessions.Count - AppConfig.MaxRecentSessions);
    }

    // ---- save ----

    /// <summary>Saves the tabs to the session file (asks where when there is none yet, or <paramref name="saveAs"/>). False when cancelled or it failed.</summary>
    internal bool SaveSession(bool saveAs = false)
    {
        if (_engine is null) return false;
        string? path = saveAs ? null : _sessionPath;
        if (path is null)
        {
            path = (PickSessionToSave ?? PickSessionToSaveDialog)();
            if (path is null) return false;
        }
        if (Path.GetExtension(path).Length == 0) path += SessionFile.Extension;

        var render = new RenderConfig { Width = _engine.Resolution.Width, Height = _engine.Resolution.Height, Fps = _engine.Fps };
        var session = SessionBuilder.Create(_tabs.Items, _tabs.LiveIndex, _tabs.PreviewMode, render, path);
        try
        {
            SessionStore.Save(path, session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger.LogError(ex, "Saving the session {Path} failed", path);
            Tell($"Sesi tidak bisa disimpan di\n{path}\n\n{ex.Message}");
            return false;
        }

        _sessionPath = path;
        _sessionDirty = false;
        RememberSession(path);
        RefreshSessionChip();
        Persist();
        StatusText.Text = $"Sesi disimpan: {path}";
        _logger.LogInformation("Session saved to {Path}: {Tabs} tabs", path, session.Tabs.Count);
        return true;
    }

    private static string? PickSessionToSaveDialog()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Sesi Stagecast (*.stagecast)|*.stagecast", DefaultExt = SessionFile.Extension, AddExtension = true, FileName = "Sesi", Title = "Simpan sesi",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? PickSessionToOpenDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Sesi Stagecast (*.stagecast)|*.stagecast", CheckFileExists = true, Title = "Buka sesi" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? PickMissingFileDialog(PlannedTab tab)
    {
        bool deck = tab.Info.Kind == TabKind.PowerPoint;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = deck ? "Presentasi PowerPoint (*.pptx)|*.pptx" : "Berkas PDF (*.pdf)|*.pdf", CheckFileExists = true,
            Title = $"Cari {(tab.Saved.Source is { } s ? Path.GetFileName(s) : tab.Info.Name)}",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? PickFolderDialog()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pilih folder berisi berkas sesi" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    // ---- new ----

    /// <summary>Closes every tab and forgets the session file. Asks first when tabs would be lost; false when the operator cancelled.</summary>
    internal async Task<bool> NewSessionAsync()
    {
        if (_engine is null) return false;
        if (!ResolveUnsaved("Sesi baru")) return false;
        _applyingSession = true;
        try
        {
            await CloseAllTabsAsync();
        }
        finally
        {
            _applyingSession = false;
        }
        _sessionPath = null;
        _sessionDirty = false;
        RefreshSessionChip();
        RebuildTabList();
        UpdateNavigationState();
        StatusText.Text = "Sesi baru: belum ada tab.";
        return true;
    }

    // Before tabs go away: save the open session if it has changed (asked), or just make sure the operator means it.
    private bool ResolveUnsaved(string what)
    {
        if (_tabs.Items.Count == 0) return true;
        if (_sessionPath is not null && _sessionDirty) return ConfirmCloseSession();
        return _sessionPath is not null || Confirm($"{what}: semua tab ditutup dan yang belum disimpan hilang. Lanjutkan?");
    }

    private SaveAnswer AskSaveChangesDialog(string name)
    {
        var answer = MessageBox.Show(this, $"Sesi {name} punya perubahan yang belum disimpan. Simpan dulu?", "Stagecast", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        return answer switch { MessageBoxResult.Yes => SaveAnswer.Save, MessageBoxResult.No => SaveAnswer.Discard, _ => SaveAnswer.Cancel };
    }

    private async Task CloseAllTabsAsync()
    {
        if (_tabs.PreviewMode) await SetPreviewModeAsync(false);
        foreach (var info in _tabs.Items.ToList()) await CloseTabAsync(info.Id, forSession: true);
        _missingTabs.Clear();
        UpdateMissingBanner();
    }

    // ---- open ----

    /// <summary>Opens a session file (asks which when <paramref name="path"/> is null). False when nothing was opened.</summary>
    internal async Task<bool> OpenSessionAsync(string? path = null)
    {
        if (_engine is null) return false;
        path ??= (PickSessionToOpen ?? PickSessionToOpenDialog)();
        if (path is null) return false;

        var loaded = SessionStore.Load(path);
        if (loaded.Session is not { } session)
        {
            if (loaded.Status == SessionLoadStatus.NotFound) _recentSessions.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            Tell(loaded.Status == SessionLoadStatus.NotFound ? $"Berkas sesi tidak ditemukan:\n{path}" : $"Berkas ini bukan sesi Stagecast yang bisa dibaca:\n{path}\n\n{string.Join("\n", loaded.Notes)}");
            return false;
        }
        foreach (string note in loaded.Notes) _logger.LogWarning("Session {Path}: {Note}", path, note);

        // A session file may come from anyone: a document on the network is not looked for unless the operator agrees.
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        bool allowNetwork = NetworkGuard.IsNetworkPath(folder);
        var network = SessionPlan.NetworkSources(session);
        if (!allowNetwork && network.Count > 0)
        {
            string listed = string.Join("\n", network.Take(5)) + (network.Count > 5 ? $"\n… dan {network.Count - 5} lagi" : "");
            allowNetwork = Confirm($"Sesi ini menunjuk berkas di jaringan:\n{listed}\n\nMencari berkas di jaringan membuat Windows menghubungi server itu. Lakukan hanya bila Anda mengenal sesi ini. Cari di jaringan?");
        }
        ISessionFiles files = new NetworkGuard(new DiskFiles(), allowNetwork);
        string name = Path.GetFileName(path);
        var plan = SessionPlan.Create(session, path, files);

        bool replace = true;
        string? offer = RenderOffer(session.Render);
        bool applyRender = offer is not null;
        if (_tabs.Items.Count > 0)
        {
            bool onAir = !_noLive && _tabs.Program is not null;
            var choice = (AskOpenChoice ?? AskOpenChoiceDialog)(name, _tabs.Items.Count, session.Tabs.Count, offer, onAir, true, _sessionPath is not null && _sessionDirty);
            if (choice is null) return false;
            if (choice.SaveFirst && !SaveSession()) return false;
            replace = choice.Replace;
            applyRender = choice.Replace && choice.ApplyRender;
        }

        if (plan.MissingCount > 0)
        {
            var resolved = (AskRelink ?? AskRelinkDialog)(name, plan, files);
            if (resolved is null) return false;
            plan = resolved;
        }

        _applyingSession = true;
        int created, skipped;
        try
        {
            if (replace) await CloseAllTabsAsync();
            (created, skipped) = await CreateSessionTabsAsync(plan, replace);
        }
        finally
        {
            _applyingSession = false;
        }

        if (applyRender && plan.Render is { } render) ApplySessionRender(render);
        if (replace)
        {
            _sessionPath = path;
            _sessionDirty = false;
        }
        else if (_sessionPath is not null) _sessionDirty = true;
        RememberSession(path);
        RefreshSessionChip();
        RebuildTabList();
        UpdateNavigationState();
        Persist();
        int missing = MissingDocumentCount;
        StatusText.Text = $"Sesi dibuka: {name} · {created} tab" + (missing > 0 ? $" · {missing} berkas belum ditemukan" : "") + (skipped > 0 ? $" · {skipped} tab tidak muat (paling banyak {TabRules.MaxTabs})" : "");
        _logger.LogInformation("Session {Path} opened: {Created} tabs, {Missing} documents missing, {Skipped} skipped", path, created, missing, skipped);
        return true;
    }

    private OpenChoice? AskOpenChoiceDialog(string name, int current, int inSession, string? offer, bool onAir, bool canSaveFirst, bool saveFirstDefault)
        => SessionDialogs.AskOpenMode(this, name, current, inSession, offer, onAir, canSaveFirst, saveFirstDefault);

    private SessionPlan? AskRelinkDialog(string name, SessionPlan plan, ISessionFiles files)
        => SessionDialogs.Relink(this, name, plan, files, PickMissingFile ?? PickMissingFileDialog, PickFolder ?? PickFolderDialog);

    /// <summary>The question to ask about the saved resolution, or null when there is none or it is what runs now.</summary>
    private string? RenderOffer(RenderConfig? saved)
    {
        if (saved is null || _engine is null) return null;
        if (saved.Width == _engine.Resolution.Width && saved.Height == _engine.Resolution.Height && saved.Fps == _engine.Fps) return null;
        return $"Pakai resolusi sesi: {saved.Width} × {saved.Height} · {saved.Fps} fps (sekarang {_engine.Resolution.Width} × {_engine.Resolution.Height} · {_engine.Fps} fps). Bila ada output yang menyala, perubahan meminta konfirmasi dulu.";
    }

    private void ApplySessionRender(RenderConfig saved)
    {
        if (_engine is null) return;
        var resolution = new Resolution(saved.Width, saved.Height);
        var change = new RenderChange(resolution != _engine.Resolution ? resolution : null, saved.Fps != _engine.Fps ? saved.Fps : null);
        if (change.Resolution is not null || change.Fps is not null) RequestChange(change);
    }

    // Creates the tabs of the plan. Nothing goes on air by itself: a replaced session ends on the test pattern with its live tab selected.
    private async Task<(int Created, int Skipped)> CreateSessionTabsAsync(SessionPlan plan, bool replaced)
    {
        var engine = _engine!;
        var inUse = _tabs.Items.Select(t => t.ProfileId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var infos = plan.ToTabInfos(id => !inUse.Contains(id) && Directory.Exists(Path.Combine(ProfilesDirectory, "tab-" + id)));
        var made = new List<(TabInfo Tab, PlannedTab Planned)>();
        int skipped = 0;
        for (int i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            var added = info.Kind == TabKind.Web ? _tabs.Add(info.Url, info.ProfileId) : _tabs.AddDocument(info.Kind, info.Source);
            if (added is null) { skipped++; continue; }
            _tabs.Rename(added.Id, info.Name);
            added = _tabs.Items[_tabs.IndexOf(added.Id)];
            if (added.Kind == TabKind.Web) await engine.AddTabAsync(added.Id, added.Url, added.ProfileId);
            else engine.AddDocumentTab(added.Id, added.Kind, added.Source, added.Name);
            made.Add((added, plan.Tabs[i]));
        }

        foreach (var (tab, planned) in made.Where(m => m.Tab.Kind != TabKind.Web))
        {
            if (engine.TabById(tab.Id) is not DocumentTab document) continue;
            if (planned.IsMissing)
            {
                _missingTabs.Add(tab.Id);
                SetDocMessage(document, "Berkas tidak ditemukan. Klik Pilih berkas… untuk memilih ulang.");
            }
            else if (planned.FoundPath is null) SetDocMessage(document, "Belum ada berkas. Klik Pilih berkas…");
            else if (tab.Kind == TabKind.PowerPoint) await OpenPowerPointAsync(document, planned.FoundPath);
            else await OpenPdfAsync(document, planned.FoundPath, askPassword: false);
        }

        if (replaced && made.Count > 0)
        {
            int live = Math.Clamp(plan.ActiveTab, 0, made.Count - 1);
            await SelectTabAsync(made[live].Tab.Id);
            if (plan.PreviewMode && made.Count >= 2) await SetPreviewModeAsync(true);
            SetNoLive(true); // opening a session never puts a tab on air; the operator takes or selects one
        }
        return (made.Count, skipped);
    }

    // ---- documents that were not found ----

    private void UpdateMissingBanner()
    {
        _missingTabs.RemoveWhere(id => _tabs.IndexOf(id) < 0);
        if (_missingTabs.Count == 0)
        {
            SessionBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var names = _missingTabs.Select(id => _tabs.Items[_tabs.IndexOf(id)]).Select(t => t.Source is { } s ? Path.GetFileName(s) : t.Name).ToList();
        SessionBannerTitle.Text = names.Count == 1 ? "1 berkas belum ditemukan" : $"{names.Count} berkas belum ditemukan";
        SessionBannerDetail.Text = string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" dan {names.Count - 3} lagi" : "") + ". Tab-nya tetap ada dan bisa diisi nanti.";
        SessionBanner.Visibility = Visibility.Visible;
    }

    private void OnBannerFindFiles(object sender, RoutedEventArgs e) => _ = RelinkMissingAsync();

    private void OnBannerDismissMissing(object sender, RoutedEventArgs e)
    {
        _missingTabs.Clear();
        UpdateMissingBanner();
    }

    /// <summary>Asks again for the documents that are still missing, the way opening a session did. Returns how many tabs got a file.</summary>
    internal async Task<int> RelinkMissingAsync()
    {
        if (_engine is null) return 0;
        var tabs = _missingTabs.Where(id => _tabs.IndexOf(id) >= 0).Select(id => _tabs.Items[_tabs.IndexOf(id)]).ToList();
        if (tabs.Count == 0) return 0;
        var file = new SessionFile
        {
            Tabs = [.. tabs.Select(t => new SessionTab { Name = t.Name, Kind = TabKinds.ConfigName(t.Kind), Url = TabRules.BlankUrl, Source = t.Source })],
        };
        string anchor = _sessionPath ?? Path.Combine(_context.Paths.Root, "sesi");
        ISessionFiles files = new NetworkGuard(new DiskFiles(), NetworkGuard.IsNetworkPath(anchor));
        var plan = SessionPlan.Create(file, anchor, files);
        var resolved = (AskRelink ?? AskRelinkDialog)(_sessionPath is null ? "sesi ini" : Path.GetFileName(_sessionPath), plan, files);
        if (resolved is null) return 0;

        int fixedCount = 0;
        for (int i = 0; i < tabs.Count; i++)
        {
            if (resolved.Tabs[i].FoundPath is not { } found || _engine.TabById(tabs[i].Id) is not DocumentTab document) continue;
            if (await ApplyDocumentSourceAsync(document, found)) fixedCount++;
        }
        UpdateMissingBanner();
        return fixedCount;
    }

    // ---- menu and keys ----

    private static UIElement MenuRow(string text, string shortcut)
    {
        var grid = new Grid { MinWidth = 280 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = text });
        var key = new TextBlock { Text = shortcut, Margin = new Thickness(24, 0, 0, 0), Opacity = 0.65, FontSize = 12 };
        Grid.SetColumn(key, 1);
        grid.Children.Add(key);
        return grid;
    }

    private void OnSessionMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SessionButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string text, string shortcut, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = MenuRow(text, shortcut), IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }
        menu.Items.Add(Item("Sesi baru", "Ctrl+N", () => _ = NewSessionAsync(), _engine is not null));
        menu.Items.Add(Item("Buka sesi…", "Ctrl+O", () => _ = OpenSessionAsync(), _engine is not null));
        menu.Items.Add(Item("Simpan", "Ctrl+S", () => SaveSession(), _engine is not null));
        menu.Items.Add(Item("Simpan sebagai…", "Ctrl+Shift+S", () => SaveSession(saveAs: true), _engine is not null));
        if (_recentSessions.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Terakhir dibuka", IsEnabled = false });
            foreach (string recent in _recentSessions)
            {
                var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(recent), ToolTip = recent };
                item.Click += (_, _) => _ = OpenSessionAsync(recent);
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Buka folder sesi", "", OpenSessionFolder));
        menu.IsOpen = true;
    }

    private void OpenSessionFolder()
    {
        string? folder = _sessionPath is not null ? Path.GetDirectoryName(_sessionPath) : _recentSessions.Count > 0 ? Path.GetDirectoryName(_recentSessions[0]) : null;
        folder ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not open the folder {Path}", folder);
        }
    }

    /// <summary>Ctrl+N, Ctrl+O, Ctrl+S and Ctrl+Shift+S. True when the key was a session key.</summary>
    private bool TrySessionKey(Key key, bool ctrl, bool shift)
    {
        if (!ctrl || _engine is null) return false;
        switch (key)
        {
            case Key.N when !shift: _ = NewSessionAsync(); return true;
            case Key.O when !shift: _ = OpenSessionAsync(); return true;
            case Key.S: SaveSession(saveAs: shift); return true;
            default: return false;
        }
    }

    // Closing the window with a saved session that has changed: offer to save it.
    private bool ConfirmCloseSession()
    {
        if (_sessionPath is null || !_sessionDirty || _tabs.Items.Count == 0) return true;
        var answer = (AskSaveChanges ?? AskSaveChangesDialog)(Path.GetFileName(_sessionPath));
        if (answer == SaveAnswer.Cancel) return false;
        return answer != SaveAnswer.Save || SaveSession();
    }
}
