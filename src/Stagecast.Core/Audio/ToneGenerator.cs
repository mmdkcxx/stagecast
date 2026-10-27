namespace Stagecast.Core.Audio;

public enum ToneChannel
{
    Both,
    Left,
    Right,
}

/// <summary>A 1 kHz sine at -20 dBFS for checking the signal path. Left or right only is for checking the channel order downstream.</summary>
public sealed class ToneGenerator
{
    public const double Frequency = 1000;
    public static readonly double Amplitude = Math.Pow(10, -20.0 / 20.0);

    private double _phase;

    public ToneChannel Channel { get; set; } = ToneChannel.Both;

    /// <summary>Overwrites the first <paramref name="frames"/> frames of <paramref name="destination"/>.</summary>
    public void Fill(float[] destination, int frames)
    {
        double step = 2 * Math.PI * Frequency / AudioFormat.SampleRate;
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(Math.Sin(_phase) * Amplitude);
            _phase += step;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            destination[i * 2] = Channel == ToneChannel.Right ? 0 : v;
            destination[i * 2 + 1] = Channel == ToneChannel.Left ? 0 : v;
        }
    }
}
