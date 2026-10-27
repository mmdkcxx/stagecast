namespace Stagecast.Core.Frames;

/// <summary>
/// One rendered picture in 8-bit BGRA. Frames are large (2520x1080 is 10.9 MB) and arrive up to 60 times a second, so the pixel
/// buffers are pooled and shared by reference counting: whoever needs a frame beyond the call that handed it over calls
/// <see cref="Retain"/> and later <see cref="Release"/>. When the last reference is released the buffer goes back to the pool.
/// </summary>
public sealed class Frame
{
    private readonly FramePool _pool;
    private int _references = 1;

    internal Frame(FramePool pool, byte[] pixels, int width, int height, int stride)
    {
        _pool = pool;
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per row; can be larger than <c>Width * 4</c>.</summary>
    public int Stride { get; }

    /// <summary>Set by the producer; the hub uses it to measure how old a frame is when it is delivered.</summary>
    public long CapturedAt { get; set; }

    public long Sequence { get; set; }

    /// <summary>Takes another reference. The caller must call <see cref="Release"/> exactly once.</summary>
    public void Retain()
    {
        if (Interlocked.Increment(ref _references) <= 1)
            throw new InvalidOperationException("The frame was already released.");
    }

    public void Release()
    {
        int left = Interlocked.Decrement(ref _references);
        if (left == 0) _pool.Return(this);
        else if (left < 0) throw new InvalidOperationException("The frame was released more often than it was retained.");
    }

    internal int References => Volatile.Read(ref _references);
}
