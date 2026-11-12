namespace PaperPilot.Core;

/// <summary>
/// Voice levels for the bubble's "cloud trail": the newest level sits right behind the plane and
/// older ones drift back and fade. Levels rise instantly and fall away gently so speech reads as
/// a smooth trail rather than flicker.
/// </summary>
public sealed class TrailBuffer
{
    private readonly double[] _samples;
    private double _pending;
    private double _held;

    public TrailBuffer(int length = 26)
    {
        _samples = new double[Math.Max(2, length)];
    }

    public int Length => _samples.Length;

    /// <summary>Level at position i (0 = right behind the plane), 0..1.</summary>
    public double this[int i] => _samples[i];

    public bool IsSilent => _held < 0.004 && _samples.All(s => s < 0.004);

    /// <summary>Report a microphone level (0..1). Can be called often; the loudest since the last tick wins.</summary>
    public void Push(double level) => _pending = Math.Max(_pending, Math.Clamp(level, 0, 1));

    /// <summary>Advance one frame: shift the trail back and add the newest level.</summary>
    public void Tick(double decay = 0.78)
    {
        _held = Math.Max(_pending, _held * decay);
        _pending = 0;
        Array.Copy(_samples, 0, _samples, 1, _samples.Length - 1);
        _samples[0] = _held < 0.004 ? 0 : _held;
    }

    public void Clear()
    {
        Array.Clear(_samples);
        _pending = _held = 0;
    }
}
