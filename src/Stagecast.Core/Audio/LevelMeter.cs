namespace Stagecast.Core.Audio;

public readonly record struct Levels(double LeftDb, double RightDb)
{
    public static readonly Levels Silent = new(LevelMeter.FloorDb, LevelMeter.FloorDb);
}

/// <summary>Peak level per channel in dBFS with a hold that falls back slowly, so a short peak is still visible on a 20 Hz display.</summary>
public sealed class LevelMeter
{
    public const double FloorDb = -90;

    private readonly object _gate = new();
    private double _left = FloorDb, _right = FloorDb;

    public static double ToDb(float peak) => peak <= 1e-5f ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(peak));

    public void Update(float[] samples, int frames, double seconds)
    {
        float l = 0, r = 0;
        for (int i = 0; i < frames; i++)
        {
            l = Math.Max(l, Math.Abs(samples[i * 2]));
            r = Math.Max(r, Math.Abs(samples[i * 2 + 1]));
        }
        double fall = 30 * seconds; // dB per second
        lock (_gate)
        {
            _left = Math.Max(ToDb(l), _left - fall);
            _right = Math.Max(ToDb(r), _right - fall);
        }
    }

    public Levels Read()
    {
        lock (_gate) return new Levels(_left, _right);
    }

    public void Reset()
    {
        lock (_gate) _left = _right = FloorDb;
    }
}
