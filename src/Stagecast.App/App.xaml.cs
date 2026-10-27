using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Stagecast.Core.Configuration;

namespace Stagecast.App;

public partial class App : Application
{
    private ILogger<App>? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.Resolve(ArgValue(e.Args, "--data-dir"));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(paths.LogDirectory, "stagecast-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        using var factory = new SerilogLoggerFactory(Log.Logger);
        _logger = factory.CreateLogger<App>();
        DispatcherUnhandledException += OnUnhandledException;

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        _logger.LogInformation("Stagecast {Version} starting; data folder {Root}", version, paths.Root);

        var result = new ConfigStore(paths.ConfigFile, factory.CreateLogger<ConfigStore>()).Load();
        var render = result.Config.Render;
        _logger.LogInformation("Config {Status}: {Width}x{Height} at {Fps} fps, {Presets} custom presets", result.Status, render.Width, render.Height, render.Fps, result.Config.Presets.Count);

        var window = new MainWindow(version, paths, result);
        window.Show();
        _logger.LogInformation("Main window shown");

        if (e.Args.Contains("--autoclose"))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { timer.Stop(); _logger.LogInformation("Autoclose"); window.Close(); };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Stagecast exiting with code {Code}", e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled exception");
        MessageBox.Show($"Terjadi kesalahan yang tidak terduga:\n\n{e.Exception.Message}\n\nRincian ada di berkas log.", "Stagecast", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
