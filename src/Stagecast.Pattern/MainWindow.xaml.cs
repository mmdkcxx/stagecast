using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Stagecast.Core.Frames;
using Frame = Stagecast.Core.Frames.Frame;

namespace Stagecast.Pattern;

public partial class MainWindow : Window
{
    private readonly PatternSettings _settings = PatternSettings.Load();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new();
    private TestPattern _pattern;
    private string _patternTitle, _patternSubtitle;
    private WriteableBitmap? _bitmap;
    private CancellationTokenSource? _cancel;
    private bool _valid, _rendering, _closeWhenDone, _loading = true;

    public MainWindow()
    {
        InitializeComponent();
        _patternTitle = _settings.Title;
        _patternSubtitle = _settings.Subtitle;
        _pattern = new TestPattern(_patternTitle, _patternSubtitle);

        foreach (int fps in PatternSettings.FpsChoices)
        {
            var choice = new RadioButton { Content = fps.ToString(CultureInfo.InvariantCulture), Tag = fps, Style = (Style)FindResource("Choice"), IsChecked = fps == _settings.Fps, GroupName = "fps" };
            choice.Checked += OnFpsChecked;
            FpsRow.Children.Add(choice);
        }

        WidthBox.Text = _settings.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = _settings.Height.ToString(CultureInfo.InvariantCulture);
        SecondsBox.Text = _settings.Seconds.ToString(CultureInfo.InvariantCulture);
        TitleBox.Text = _settings.Title;
        SubtitleBox.Text = _settings.Subtitle;
        ToneBox.Text = _settings.ToneHz.ToString(CultureInfo.InvariantCulture);
        LevelBox.Text = _settings.LevelDb.ToString("0.#", CultureInfo.InvariantCulture);
        _loading = false;
        ReadFields();

        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += (_, _) => DrawPreview();
        _timer.Start();
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) ReadFields();
    }

    private void OnFpsChecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Fps = (int)((RadioButton)sender).Tag;
        ReadFields();
    }

    // Reads every field; shows what is wrong under the field and only allows Render when everything is fine.
    private void ReadFields()
    {
        bool Int(TextBox box, out int value) => int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        string? sizeError = null, secondsError = null, soundError = null;
        bool wOk = Int(WidthBox, out int w), hOk = Int(HeightBox, out int h);
        if (!wOk || !hOk) sizeError = "Isi lebar dan tinggi dengan angka bulat";
        else sizeError = PatternSettings.CheckSize(w, "Lebar") ?? PatternSettings.CheckSize(h, "Tinggi");
        if (!Int(SecondsBox, out int seconds)) secondsError = "Isi durasi dengan angka bulat";
        else secondsError = PatternSettings.CheckSeconds(seconds);
        bool toneOk = Int(ToneBox, out int tone);
        bool levelOk = double.TryParse(LevelBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double level);
        if (!toneOk || !levelOk) soundError = "Isi nada dan level dengan angka";
        else soundError = PatternSettings.CheckTone(tone) ?? PatternSettings.CheckLevel(level);

        SizeError.Text = sizeError ?? "";
        SecondsError.Text = secondsError ?? "";
        SoundError.Text = soundError ?? "";
        _valid = sizeError is null && secondsError is null && soundError is null;
        RenderButton.IsEnabled = _valid || _rendering;
        if (!_valid) return;

        _settings.Width = w;
        _settings.Height = h;
        _settings.Seconds = seconds;
        _settings.ToneHz = tone;
        _settings.LevelDb = level;
        _settings.Title = TestPattern.Sanitize(TitleBox.Text.Trim());
        _settings.Subtitle = TestPattern.Sanitize(SubtitleBox.Text.Trim());
        if (_settings.Title != _patternTitle || _settings.Subtitle != _patternSubtitle)
        {
            (_patternTitle, _patternSubtitle) = (_settings.Title, _settings.Subtitle);
            _pattern = new TestPattern(_patternTitle, _patternSubtitle);
        }

        int gcd = Gcd(w, h);
        MetaSize.Text = $"Pratinjau {w}×{h}";
        MetaRatio.Text = $"Rasio {w / gcd}:{h / gcd}";
        MetaFrames.Text = $"{_settings.TotalFrames:N0} frame".Replace(',', '.');
        MetaBytes.Text = $"Ukuran paling besar ± {Math.Max(1, _settings.EstimatedBytes() / 1_000_000)} MB";
        FileName.Text = SuggestedName(_settings);
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    private static string SuggestedName(PatternSettings s) => $"pattern_{s.Width}x{s.Height}_{s.Fps}fps_{s.Seconds}s.mp4";

    private void DrawPreview()
    {
        if (!_valid || _rendering) return;
        int w = _settings.Width, h = _settings.Height;
        Frame frame = _pattern.Render(w, h, _settings.Fps, _clock.Elapsed);
        try
        {
            if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                Preview.Source = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), frame.Pixels, frame.Stride, 0);
        }
        finally { frame.Release(); }
    }

    private async void OnRenderClick(object sender, RoutedEventArgs e)
    {
        if (_rendering)
        {
            _cancel?.Cancel();
            return;
        }

        if (!_valid) return;
        PatternSettings job = _settings.Clone();
        var dialog = new SaveFileDialog
        {
            Title = "Simpan video test pattern",
            Filter = "Video MP4 (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            FileName = SuggestedName(job),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        _settings.Save();
        string path = dialog.FileName;
        _rendering = true;
        _cancel = new CancellationTokenSource();
        Controls.IsEnabled = false;
        RenderButton.Content = "Batal";
        Bar.Value = 0;
        var clock = Stopwatch.StartNew();
        var progress = new Progress<long>(n =>
        {
            Bar.Value = (double)n / job.TotalFrames;
            double rate = n / Math.Max(0.1, clock.Elapsed.TotalSeconds);
            StatusText.Text = $"Frame {n:N0} / {job.TotalFrames:N0}  ·  {rate:0} frame/detik  ·  sisa ± {TimeSpan.FromSeconds((job.TotalFrames - n) / Math.Max(1, rate)):mm\\:ss}".Replace(',', '.');
        });

        string result;
        bool ok = false;
        try
        {
            await Task.Run(() => PatternJob.Run(job, path, progress, _cancel.Token));
            ok = true;
            result = $"Selesai dalam {clock.Elapsed:mm\\:ss}: {path} ({new FileInfo(path).Length / 1_000_000} MB)";
        }
        catch (OperationCanceledException)
        {
            result = "Dibatalkan. Berkas sebagian dihapus.";
        }
        catch (Exception ex)
        {
            result = $"Gagal: {ex.Message}";
        }

        if (!ok) TryDelete(path);
        _rendering = false;
        _cancel.Dispose();
        _cancel = null;
        Controls.IsEnabled = true;
        RenderButton.Content = "Render MP4";
        StatusText.Text = result;
        if (!ok) Bar.Value = 0;
        if (_closeWhenDone) Close();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* leave it */ }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_rendering)
        {
            // Let the render stop and clean up its file first.
            _closeWhenDone = true;
            _cancel?.Cancel();
            e.Cancel = true;
            return;
        }

        _settings.Save();
        base.OnClosing(e);
    }
}
