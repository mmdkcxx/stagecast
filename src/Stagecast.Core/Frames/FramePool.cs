namespace Stagecast.Core.Frames;

/// <summary>Reuses pixel buffers so that 60 frames per second do not become 650 MB per second of garbage.</summary>
public sealed class FramePool
{
    private const int MaxIdleBuffers = 6;

    private readonly object _gate = new();
    private readonly Stack<byte[]> _idle = new();
    private int _bufferSize;

    /// <summary>Number of buffers ever allocated; tests and statistics use it to see that reuse works.</summary>
    public int Allocated { get; private set; }

    public Frame Rent(int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4) throw new ArgumentOutOfRangeException(nameof(width), "invalid frame geometry");
        int size = checked(stride * height);
        byte[]? buffer = null;
        lock (_gate)
        {
            if (size != _bufferSize)
            {
                // The resolution changed: buffers of the old size are of no use any more.
                _idle.Clear();
                _bufferSize = size;
            }
            if (_idle.Count > 0) buffer = _idle.Pop();
            else Allocated++;
        }
        return new Frame(this, buffer ?? new byte[size], width, height, stride);
    }

    internal void Return(Frame frame)
    {
        lock (_gate)
        {
            if (frame.Pixels.Length == _bufferSize && _idle.Count < MaxIdleBuffers) _idle.Push(frame.Pixels);
        }
    }
}
