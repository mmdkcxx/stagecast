using Microsoft.Extensions.Logging;
using Stagecast.Core.Audio;

namespace Stagecast.Rendering.Audio;

/// <summary>
/// Keeps one process-loopback capture per WebView2 browser process and feeds each into the <see cref="AudioHub"/>. Tabs of the
/// same profile share a process; which tab is heard is decided by <c>IsMuted</c> on the tabs, not here.
/// </summary>
public sealed class BrowserAudioCapture : IDisposable
{
    private readonly AudioHub _hub;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, ProcessLoopbackCapture> _captures = [];
    private readonly HashSet<int> _failed = [];

    public BrowserAudioCapture(AudioHub hub, ILogger logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public int Count
    {
        get { lock (_gate) return _captures.Count; }
    }

    /// <summary>Captures new processes, releases those that are gone. A process that could not be captured is not retried until it disappears from the list.</summary>
    public void Sync(IEnumerable<int> processIds)
    {
        var wanted = processIds.Where(id => id > 0).ToHashSet();
        lock (_gate)
        {
            foreach (int gone in _captures.Keys.Where(id => !wanted.Contains(id)).ToList())
            {
                _captures[gone].Dispose();
                _captures.Remove(gone);
                _hub.RemoveSource(KeyOf(gone));
                _logger.LogInformation("Audio capture of browser process {Pid} stopped", gone);
            }
            _failed.RemoveWhere(id => !wanted.Contains(id));
            foreach (int pid in wanted)
            {
                if (_captures.ContainsKey(pid) || _failed.Contains(pid)) continue;
                var ring = _hub.AddSource(KeyOf(pid));
                try
                {
                    _captures[pid] = new ProcessLoopbackCapture(pid, (samples, frames) => ring.Write(samples, frames));
                    _logger.LogInformation("Audio capture of browser process {Pid} started", pid);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    _failed.Add(pid);
                    _hub.RemoveSource(KeyOf(pid));
                    _logger.LogWarning(ex, "Audio capture of browser process {Pid} failed", pid);
                }
            }
        }
    }

    public int FailedCount
    {
        get { lock (_gate) return _failed.Count; }
    }

    private static string KeyOf(int pid) => $"browser-{pid}";

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (pid, capture) in _captures)
            {
                capture.Dispose();
                _hub.RemoveSource(KeyOf(pid));
            }
            _captures.Clear();
        }
    }
}
