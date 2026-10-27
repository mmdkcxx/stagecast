namespace Stagecast.Core.Display;

/// <summary>
/// Which part of the rendered page the operator's preview needs, and how much it may be shrunk: the region is in page pixels,
/// <see cref="Mip"/> halves the size that many times (0 is full resolution, 2 is a quarter of the width and height).
/// </summary>
public readonly record struct PreviewRegion(int X, int Y, int Width, int Height, int Mip)
{
    public const int MaxMip = 4;
}
