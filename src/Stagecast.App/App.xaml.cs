using System.IO;
using System.Reflection;
using System.Threading.Tasks;
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
    private bool _selfTest;
    private System.Threading.Timer? _watchdog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.Resolve(ArgValue(e.Args, "--data-dir"));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(paths.LogDirectory, "stagecast-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        var factory = new SerilogLoggerFactory(Log.Logger);
        _logger = factory.CreateLogger<App>();
        _selfTest = e.Args.Contains("--selftest");
        if (_selfTest)
        {
            // A dialog or a hang must never keep an unattended test (and the script waiting for it) alive forever. This runs on its own
            // thread, so it works even when the window is blocked by a message box. STAGECAST_SOAK_MINUTES lengthens it for soak runs.
            int soak = int.TryParse(Environment.GetEnvironmentVariable("STAGECAST_SOAK_MINUTES"), out int m) && m > 0 ? m : 0;
            int minutes = Math.Max(12, soak + 10);
            _watchdog = new System.Threading.Timer(_ =>
            {
                Log.Fatal("SELFTEST TIMEOUT after {Minutes} minutes: a dialog or a hang kept the test from finishing; ending the process", minutes);
                Log.CloseAndFlush();
                Environment.Exit(3);
            }, null, TimeSpan.FromMinutes(minutes), System.Threading.Timeout.InfiniteTimeSpan);
        }
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        _logger.LogInformation("Stagecast {Version} starting; data folder {Root}", version, paths.Root);

        var result = new ConfigStore(paths.ConfigFile, factory.CreateLogger<ConfigStore>()).Load();
        var render = result.Config.Render;
        _logger.LogInformation("Config {Status}: {Width}x{Height} at {Fps} fps, {Presets} custom presets", result.Status, render.Width, render.Height, render.Fps, result.Config.Presets.Count);

        var store = new ConfigStore(paths.ConfigFile, factory.CreateLogger<ConfigStore>());
        var window = new MainWindow(new StartupContext(version, paths, store, result, factory, ArgValue(e.Args, "--url"), ArgValue(e.Args, "--selftest")));
        window.Show();
        _logger.LogInformation("Main window shown");

        if (e.Args.Contains("--autoclose"))
        {
            // With a self test the window closes once the test is done (a few seconds after it starts logging).
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(e.Args.Contains("--selftest") ? 180 : 4) };
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
        if (_selfTest) { e.Handled = true; return; } // no message box under an unattended test; the log has it
        MessageBox.Show($"Terjadi kesalahan yang tidak terduga:\n\n{e.Exception.Message}\n\nRincian ada di berkas log.", "Stagecast", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    // Exceptions on other threads end the process; the log gets them before it goes.
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception on a background thread (terminating: {Terminating}): {Object}", e.IsTerminating, e.ExceptionObject);
        Log.CloseAndFlush();
    }

    // A faulted task nobody awaited: the process goes on, but the failure should not be silent.
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
