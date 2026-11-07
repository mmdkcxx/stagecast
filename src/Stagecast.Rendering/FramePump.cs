using Microsoft.Extensions.Logging;
using Stagecast.Core.Frames;
using Stagecast.Rendering.Interop;

namespace Stagecast.Rendering;

/// <summary>Calls <see cref="FrameHub.Tick"/> at a steady rate on its own thread. The rate can change while running.</summary>
public sealed class FramePump : IDisposable
{
    private readonly FrameHub _hub;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private volatile bool _stop;
    private int _fps;

    public FramePump(FrameHub hub, int fps, ILogger logger)
    {
        _hub = hub;
        _fps = fps;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "frame-pump", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public int Fps
    {
        get => Volatile.Read(ref _fps);
        set => Volatile.Write(ref _fps, value);
    }

    private void Run()
    {
        using var timer = new HighResolutionTimer();
        long frequency = System.Diagnostics.Stopwatch.Frequency;
        long next = System.Diagnostics.Stopwatch.GetTimestamp();
        int lastFps = Fps;
        while (!_stop)
        {
            int fps = Fps;
            long interval = frequency / fps;
            if (fps != lastFps) { next = System.Diagnostics.Stopwatch.GetTimestamp(); lastFps = fps; }

            // The hub asks for small shifts so the ticks do not run past the frames the capture publishes (see PhaseLock).
            double correctionMs = _hub.TakePhaseCorrectionMs();
            next += interval + (long)(correctionMs * frequency / 1000.0);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - next > 2 * interval) next = now; // far behind (machine stalled): resynchronise instead of bursting
            else if (next > now) timer.Wait(TimeSpan.FromSeconds((double)(next - now) / frequency));

            try
            {
                _hub.Tick();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Frame tick failed");
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
    }
}
