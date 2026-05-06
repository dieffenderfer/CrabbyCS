namespace MouseHouse.Core;

/// <summary>
/// Procedural "spectrum analyzer" — a faked frequency display that bounces
/// like a real one without actually probing the audio stream. We can't read
/// samples back from the ffplay subprocess, so the bars are driven by a
/// summed-sines model with a periodic bass beat for that classic equaliser
/// feel: bass-heavy on the low end, busier on mid, sparser high spikes.
/// </summary>
public class SpectrumVisualizer
{
    private readonly float[] _bars;
    private readonly float[] _peaks;
    private readonly float[] _phases;
    private float _time;
    private float _beatTimer;
    private float _beatStrength;

    public int BandCount => _bars.Length;
    public float Bar(int i) => _bars[i];
    public float Peak(int i) => _peaks[i];

    public SpectrumVisualizer(int bandCount)
    {
        _bars = new float[bandCount];
        _peaks = new float[bandCount];
        _phases = new float[bandCount];
        var rng = new Random();
        for (int i = 0; i < bandCount; i++)
            _phases[i] = (float)rng.NextDouble() * MathF.PI * 2;
    }

    public void Tick(float delta, bool active)
    {
        _time += delta;

        // Beat envelope: re-arm every ~0.4 s with a little jitter.
        if (active)
        {
            _beatTimer += delta;
            float period = 0.42f + 0.05f * MathF.Sin(_time * 0.3f);
            if (_beatTimer >= period)
            {
                _beatTimer -= period;
                _beatStrength = 1.0f;
            }
            _beatStrength = MathF.Max(0, _beatStrength - delta * 4f);
        }
        else
        {
            _beatStrength = 0;
            _beatTimer = 0;
        }

        for (int i = 0; i < _bars.Length; i++)
        {
            float target = active ? ComputeTarget(i, _beatStrength) : 0;
            float diff = target - _bars[i];
            _bars[i] += diff * MathF.Min(1, delta * 12);

            // Peak hold + slow drop
            if (_bars[i] > _peaks[i]) _peaks[i] = _bars[i];
            else _peaks[i] = MathF.Max(0, _peaks[i] - delta * 0.55f);
        }
    }

    private float ComputeTarget(int i, float beat)
    {
        float band = (_bars.Length <= 1) ? 0f : (float)i / (_bars.Length - 1);
        // Bass-weighted beat impulse (low end thumps strongest)
        float bass = (1 - MathF.Min(1, band * 1.6f)) * 0.65f * beat;
        // Per-bar wobble (sum of two sines with that bar's phase offset)
        float wobble = 0.10f
            + 0.18f * MathF.Sin(_time * (1.2f + i * 0.27f) + _phases[i])
            + 0.10f * MathF.Sin(_time * 3.1f + _phases[i] * 1.7f);
        wobble = MathF.Max(0, wobble);
        // Sparse high-frequency spikes
        float spike = 0;
        if (band > 0.4f)
        {
            float n = 0.5f + 0.5f * MathF.Sin(i * 12.9898f + _time * 11.7f);
            if (n > 0.85f) spike = (n - 0.85f) * 4f * (0.4f + 0.6f * (1 - band));
        }
        return Math.Clamp(bass + wobble + spike, 0, 1);
    }
}
