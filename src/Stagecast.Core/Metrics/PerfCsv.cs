using System.Globalization;

namespace Stagecast.Core.Metrics;

/// <summary>One row of the performance run: what the pipeline did during one phase of the scenario.</summary>
/// <param name="Copy">Full-frame GPU-to-CPU copy for the output; only average and maximum are filled.</param>
/// <param name="Handler">The whole capture callback for one compositor frame.</param>
/// <param name="LockWait">Waiting for the shared Direct3D lock.</param>
/// <param name="PreviewCapture">The preview part of the capture callback, including building the picture for the window.</param>
/// <param name="PreviewLag">Time from reading a preview picture to showing it.</param>
/// <param name="GpuPercent">Null when the GPU counters are not available on this machine.</param>
/// <param name="HubAgeAvgMs">Average age of a frame when the output tick sends it (since the capture asked the GPU for it): the output latency.</param>
public sealed record PerfSample(
    string Phase, double Seconds, double OutFps, long Repeated, long Dropped, double CompositorFps,
    TimingSnapshot Copy, TimingSnapshot Handler, TimingSnapshot LockWait, TimingSnapshot PreviewCapture,
    double PreviewShownFps, double PreviewSkippedFps, TimingSnapshot PreviewLag, double TickP99Ms,
    double AppCpuPercent, double WebViewCpuPercent, double? GpuPercent, int NdiConnections, int OmtConnections,
    double HubAgeAvgMs = 0, double HubAgeMaxMs = 0);

public static class PerfCsv
{
    public static string Header { get; } = string.Join(',',
        "phase", "seconds", "out_fps", "repeated", "dropped", "compositor_fps",
        "copy_avg_ms", "copy_max_ms",
        "handler_avg_ms", "handler_p99_ms", "handler_max_ms",
        "lock_wait_avg_ms", "lock_wait_p99_ms", "lock_wait_max_ms",
        "preview_capture_avg_ms", "preview_capture_p99_ms", "preview_capture_max_ms",
        "preview_shown_fps", "preview_skipped_fps",
        "preview_lag_avg_ms", "preview_lag_p99_ms", "preview_lag_max_ms",
        "tick_p99_ms", "app_cpu_percent", "webview_cpu_percent", "gpu_percent", "ndi_connections", "omt_connections",
        "hub_age_avg_ms", "hub_age_max_ms");

    public static string Line(PerfSample s) => string.Join(',',
        s.Phase.Replace(',', ';'), N(s.Seconds), N(s.OutFps), s.Repeated.ToString(CultureInfo.InvariantCulture), s.Dropped.ToString(CultureInfo.InvariantCulture), N(s.CompositorFps),
        N(s.Copy.AverageMs), N(s.Copy.MaxMs),
        N(s.Handler.AverageMs), N(s.Handler.P99Ms), N(s.Handler.MaxMs),
        N(s.LockWait.AverageMs), N(s.LockWait.P99Ms), N(s.LockWait.MaxMs),
        N(s.PreviewCapture.AverageMs), N(s.PreviewCapture.P99Ms), N(s.PreviewCapture.MaxMs),
        N(s.PreviewShownFps), N(s.PreviewSkippedFps),
        N(s.PreviewLag.AverageMs), N(s.PreviewLag.P99Ms), N(s.PreviewLag.MaxMs),
        N(s.TickP99Ms), N(s.AppCpuPercent), N(s.WebViewCpuPercent), s.GpuPercent is { } g ? N(g) : "",
        s.NdiConnections.ToString(CultureInfo.InvariantCulture), s.OmtConnections.ToString(CultureInfo.InvariantCulture),
        N(s.HubAgeAvgMs), N(s.HubAgeMaxMs));

    // A broken counter must never put NaN or Infinity into the file.
    private static string N(double value) => double.IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : "0";
}
