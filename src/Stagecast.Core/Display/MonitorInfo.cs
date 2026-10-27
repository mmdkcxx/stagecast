using Stagecast.Core.Rendering;

namespace Stagecast.Core.Display;

/// <summary>A monitor as the operator sees it. Position and size are physical pixels in the virtual desktop.</summary>
/// <param name="Id">Windows device name (for example <c>\\.\DISPLAY2</c>); stable enough to find the same monitor again after a restart.</param>
public sealed record MonitorInfo(string Id, int Number, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public Resolution Size => new(Width, Height);

    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;

    public string Label => $"Monitor {Number}: {Width}×{Height}{(IsPrimary ? " (utama)" : string.Empty)}";
}
