namespace Stagecast.Core.Rendering;

/// <summary>A render size in pixels. The bounds are a proposal from the UI mockup and still unverified against WebView2/GPU limits.</summary>
public readonly record struct Resolution(int Width, int Height)
{
    public const int MinSide = 100;
    public const int MaxSide = 8192;

    public static bool IsValidSide(int value) => value is >= MinSide and <= MaxSide;

    public static bool TryCreate(int width, int height, out Resolution resolution)
    {
        resolution = new Resolution(width, height);
        return IsValidSide(width) && IsValidSide(height);
    }

    public override string ToString() => $"{Width} × {Height}";
}
