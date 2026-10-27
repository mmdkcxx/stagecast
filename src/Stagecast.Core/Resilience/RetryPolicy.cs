namespace Stagecast.Core.Resilience;

/// <summary>How long to wait before trying to load a page again after the network failed. Quick at first, then steady.</summary>
public static class RetryPolicy
{
    private static readonly int[] Seconds = [2, 5, 10, 20, 30];

    /// <summary>The wait before retry number <paramref name="attempt"/> (0 is the first retry). Never longer than 30 seconds, so a returning network is noticed soon.</summary>
    public static TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(Seconds[Math.Clamp(attempt, 0, Seconds.Length - 1)]);

    /// <summary>Render-process crashes allowed in <see cref="CrashWindow"/> before the application stops reloading by itself and tells the operator.</summary>
    public const int MaxCrashesInWindow = 5;

    public static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(2);

    /// <summary>True when another automatic reload is allowed, given when the earlier crashes happened.</summary>
    public static bool AllowReload(IReadOnlyCollection<DateTimeOffset> earlierCrashes, DateTimeOffset now)
        => earlierCrashes.Count(t => now - t < CrashWindow) < MaxCrashesInWindow;
}
