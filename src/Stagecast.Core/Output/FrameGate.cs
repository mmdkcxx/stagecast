namespace Stagecast.Core.Output;

public enum GateMode
{
    /// <summary>Every new frame goes out.</summary>
    Live,

    /// <summary>The last live frame is repeated; the page keeps running behind it.</summary>
    Freeze,

    /// <summary>A black frame goes out.</summary>
    Blackout,
}

/// <summary>
/// Decides which frame an output sends. Capture and page input keep running in every mode; only what leaves the
/// application changes. Leaving Freeze or Blackout shows the newest frame on the very next call.
/// </summary>
public sealed class FrameGate<T> where T : class
{
    private readonly T _black;
    private T? _lastLive;

    public FrameGate(T blackFrame) => _black = blackFrame;

    public GateMode Mode { get; set; } = GateMode.Live;

    /// <summary>Returns the frame to send for <paramref name="incoming"/>, or null if there is nothing to show yet.</summary>
    public T? Select(T incoming)
    {
        switch (Mode)
        {
            case GateMode.Live:
                _lastLive = incoming;
                return incoming;
            case GateMode.Freeze:
                return _lastLive;
            default:
                return _black;
        }
    }
}
