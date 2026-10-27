using Stagecast.Core.Resilience;

namespace Stagecast.Core.Tests;

public class RetryPolicyTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 30)]
    [InlineData(99, 30)]
    [InlineData(-3, 2)]
    public void Delay_GrowsThenStaysAt30Seconds(int attempt, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), RetryPolicy.Delay(attempt));

    [Fact]
    public void AllowReload_StopsAfterTooManyRecentCrashes()
    {
        var now = DateTimeOffset.UtcNow;
        var crashes = Enumerable.Range(0, RetryPolicy.MaxCrashesInWindow).Select(i => now - TimeSpan.FromSeconds(10 * i)).ToList();
        Assert.False(RetryPolicy.AllowReload(crashes, now));
        Assert.True(RetryPolicy.AllowReload(crashes.Take(RetryPolicy.MaxCrashesInWindow - 1).ToList(), now));
    }

    [Fact]
    public void AllowReload_IgnoresOldCrashes()
    {
        var now = DateTimeOffset.UtcNow;
        var old = Enumerable.Range(0, 10).Select(_ => now - RetryPolicy.CrashWindow - TimeSpan.FromSeconds(1)).ToList();
        Assert.True(RetryPolicy.AllowReload(old, now));
    }
}
