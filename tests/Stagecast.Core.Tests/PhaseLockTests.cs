using Stagecast.Core.Frames;

namespace Stagecast.Core.Tests;

public class PhaseLockTests
{
    private const double I = 1000.0 / 60;

    /// <summary>
    /// Ticks run on their own schedule (moved by the lock's corrections), frames arrive every interval at a fixed phase plus jitter.
    /// Returns how many ticks (after the warm-up) found no frame or more than one, the slack at the end, and the largest step taken.
    /// </summary>
    private static (int Bad, double Slack, double MaxStep, int Ticks) Simulate(PhaseLock? pll, double publishPhaseMs, double jitterMs, int ticks, int warmup, int seed = 7)
    {
        var random = new Random(seed);
        double tick = 0, lastTick = 0;
        double nextPublish = publishPhaseMs + Jitter(random, jitterMs);
        int n = 0, pending = 0, bad = 0;
        double lastSlack = 0, maxStep = 0, lastArrival = 0;
        for (int k = 1; k <= ticks; k++)
        {
            double correction = pll?.TakeCorrection(tick) ?? 0;
            maxStep = Math.Max(maxStep, Math.Abs(correction));
            lastTick = tick;
            tick += I + correction;
            while (nextPublish <= tick)
            {
                pll?.AddPublish(nextPublish, nextPublish - lastTick);
                lastArrival = nextPublish;
                pending++;
                n++;
                nextPublish = publishPhaseMs + n * I + Jitter(random, jitterMs);
            }
            if (k > warmup && pending != 1) bad++;
            lastSlack = tick - lastArrival;
            pending = 0;
        }
        return (bad, lastSlack, maxStep, ticks - warmup);
    }

    private static double Jitter(Random random, double amount) => (random.NextDouble() * 2 - 1) * amount;

    [Fact]
    public void Without_the_lock_the_worst_phase_misses_about_half_the_ticks()
    {
        var result = Simulate(null, publishPhaseMs: 0.0, jitterMs: 3, ticks: 400, warmup: 60);
        Assert.True(result.Bad > 100, $"expected a bad scenario, got {result.Bad} bad ticks");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(4.0)]
    [InlineData(8.0)]
    [InlineData(12.0)]
    [InlineData(16.0)]
    public void With_the_lock_every_tick_gets_exactly_one_frame_from_any_start_phase(double publishPhase)
    {
        var result = Simulate(new PhaseLock(), publishPhase, jitterMs: 3, ticks: 500, warmup: 120);
        Assert.Equal(0, result.Bad);
        Assert.InRange(result.Slack, I * 0.5 - 4.5, I * 0.5 + 4.5); // ends near the target, within the jitter
    }

    [Fact]
    public void Each_step_is_at_most_half_a_millisecond()
    {
        var result = Simulate(new PhaseLock(), publishPhaseMs: 0.0, jitterMs: 1, ticks: 300, warmup: 0);
        Assert.True(result.MaxStep <= 0.5 + 1e-9, $"largest step {result.MaxStep}");
    }

    [Fact]
    public void An_arrival_phase_already_at_the_target_needs_no_correction()
    {
        var pll = new PhaseLock();
        for (int i = 0; i < 30; i++) pll.AddPublish(i * I, I * 0.5); // 8.3 ms after the tick = 8.3 ms before the next
        Assert.Equal(0, pll.TakeCorrection(30 * I));
    }

    [Fact]
    public void Fewer_than_eight_samples_give_no_correction()
    {
        var pll = new PhaseLock();
        for (int i = 0; i < 7; i++) pll.AddPublish(i * I, 1.0);
        Assert.Equal(0, pll.TakeCorrection(7 * I));
        pll.AddPublish(7 * I, 1.0);
        Assert.NotEqual(0, pll.TakeCorrection(8 * I));
    }

    [Fact]
    public void Arrivals_spread_over_the_whole_interval_give_no_correction()
    {
        var pll = new PhaseLock();
        for (int i = 0; i < 24; i++) pll.AddPublish(i * I, i * I / 24); // evenly spread: no pattern to lock to
        Assert.Equal(0, pll.TakeCorrection(24 * I));
    }

    [Fact]
    public void The_mean_wraps_around_the_tick_boundary()
    {
        // Arrivals alternate just after and just before the tick (0.3 ms and I-0.3 ms): the circular mean is at the tick,
        // a linear mean would be at half an interval. With target slack 0.3 the wanted position is 0.7 I = 11.7 ms,
        // so the arrivals are 5 ms too early after the previous tick (wrapped), and the ticks must be moved later by... see sign below.
        var pll = new PhaseLock(targetSlackFraction: 0.3);
        for (int i = 0; i < 24; i++) pll.AddPublish(i * I, i % 2 == 0 ? 0.3 : I - 0.3);
        double correction = pll.TakeCorrection(24 * I);
        Assert.Equal(0.5, correction, 6); // wanted 11.67, got ~0: error = 0 - 11.67 wraps to +5.0 -> delay by the step limit
    }

    [Fact]
    public void A_pause_in_publishing_forgets_the_old_phase()
    {
        var pll = new PhaseLock();
        double t = 0;
        for (int i = 0; i < 24; i++, t += I) pll.AddPublish(t, 2.0);
        t += 1000; // the page stood still
        for (int i = 0; i < 8; i++, t += I) pll.AddPublish(t, 12.0);
        Assert.Equal(0.5, pll.TakeCorrection(t), 6); // only the new phase (12 ms > 8.3 target) counts: delay
    }

    [Fact]
    public void A_pause_without_new_frames_gives_no_correction()
    {
        var pll = new PhaseLock();
        for (int i = 0; i < 24; i++) pll.AddPublish(i * I, 2.0);
        Assert.NotEqual(0, pll.TakeCorrection(24 * I));
        Assert.Equal(0, pll.TakeCorrection(24 * I + 1000));
    }

    [Fact]
    public void Changing_the_interval_forgets_the_measurements()
    {
        var pll = new PhaseLock();
        for (int i = 0; i < 24; i++) pll.AddPublish(i * I, 2.0);
        pll.IntervalMs = 1000.0 / 30;
        Assert.Equal(0, pll.TakeCorrection(24 * I));
    }

    [Fact]
    public void Applying_corrections_without_new_samples_converges_without_overshoot()
    {
        var pll = new PhaseLock();
        for (int i = 0; i < 24; i++) pll.AddPublish(i * I, 14.0); // target 8.33: 5.67 ms too late after the tick
        double sum = 0;
        int calls = 0;
        while (calls < 40)
        {
            double c = pll.TakeCorrection(24 * I);
            if (c == 0) break;
            Assert.True(c > 0);
            sum += c;
            calls++;
        }
        Assert.InRange(sum, 5.0, 5.67 + 1e-6); // stops inside the deadband, never past the target
        Assert.InRange(calls, 10, 12);
    }
}
