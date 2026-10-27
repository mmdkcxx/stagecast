using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Stagecast.Spike;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ContentRendered += OnContentRendered;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        var lines = new[]
        {
            $"runtime : {RuntimeInformation.FrameworkDescription}",
            $"os      : {RuntimeInformation.OSDescription}",
            $"window  : {ActualWidth:0} x {ActualHeight:0}",
        };
        Info.Text = string.Join(Environment.NewLine, lines);
        Log("S0 window rendered");
        foreach (var l in lines) Log(l);

        // --autoclose lets the build script verify the window without a person closing it.
        if (Environment.GetCommandLineArgs().Contains("--autoclose"))
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (_, _) => { t.Stop(); Log("S0 autoclose"); Close(); };
            t.Start();
        }
    }

    private static void Log(string message)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, "s0.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
    }
}
