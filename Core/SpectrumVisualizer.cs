namespace MouseHouse.Core;

/// <summary>
/// Spectrum analyzer driven by live PCM from <see cref="RadioPlayer"/>. Each
/// band runs a Goertzel filter at a logarithmically-spaced center frequency
/// (≈60 Hz – 12 kHz) over the most recent ~2k mono samples to extract that
/// band's energy; we log-scale the magnitude, attack-fast / release-slow into
/// <see cref="Bar"/>, and track a slow-falling <see cref="Peak"/> for the
/// classic equalizer cap-line. When live samples aren't available (legacy
/// ffplay/mpv backend, or radio off) we fall back to a sines-plus-beat
/// synthetic spectrum so the visualizers still animate. <see cref="Energy"/>
/// is the smoothed full-band average — visualizers use it for global motion
/// like bass-pumping a tunnel or swelling a starburst.
/// </summary>
public class SpectrumVisualizer
{
    private readonly float[] _bars;
    private readonly float[] _peaks;
    private readonly float[] _phases;
    private readonly float[] _bandFreq;
    private readonly float[] _coef;       // 2*cos(2π*f/fs) per band
    private float _coefSampleRate;        // fs the coef array was computed for

    private float _time;
    private float _beatTimer;
    private float _beatStrength;
    private float _energy;                // smoothed full-band average

    public int BandCount => _bars.Length;
    public float Bar(int i) => _bars[i];
    public float Peak(int i) => _peaks[i];

    /// <summary>Smoothed average of all bars in [0,1]. Useful for whole-scene pumping.</summary>
    public float Energy => _energy;

    /// <summary>Average of the lowest ~25% of bands.</summary>
    public float Bass
    {
        get
        {
            int n = Math.Max(1, _bars.Length / 4);
            float s = 0;
            for (int i = 0; i < n; i++) s += _bars[i];
            return s / n;
        }
    }

    /// <summary>Average of bands roughly 25–60% (vocals / instruments).</summary>
    public float Mid
    {
        get
        {
            int lo = _bars.Length / 4;
            int hi = (int)(_bars.Length * 0.6f);
            int n = Math.Max(1, hi - lo);
            float s = 0;
            for (int i = lo; i < hi; i++) s += _bars[i];
            return s / n;
        }
    }

    /// <summary>Average of the top ~40% of bands.</summary>
    public float Treble
    {
        get
        {
            int lo = (int)(_bars.Length * 0.6f);
            int n = Math.Max(1, _bars.Length - lo);
            float s = 0;
            for (int i = lo; i < _bars.Length; i++) s += _bars[i];
            return s / n;
        }
    }

    public SpectrumVisualizer(int bandCount)
    {
        _bars = new float[bandCount];
        _peaks = new float[bandCount];
        _phases = new float[bandCount];
        _bandFreq = new float[bandCount];
        _coef = new float[bandCount];

        // Log-spaced center frequencies from ~60 Hz to ~12 kHz (sub-bass /
        // kick territory through hi-hat / cymbal sparkle).
        const float fLo = 60f;
        const float fHi = 12000f;
        for (int i = 0; i < bandCount; i++)
        {
            float t = (bandCount <= 1) ? 0 : (float)i / (bandCount - 1);
            _bandFreq[i] = fLo * MathF.Pow(fHi / fLo, t);
        }

        var rng = new Random();
        for (int i = 0; i < bandCount; i++)
            _phases[i] = (float)rng.NextDouble() * MathF.PI * 2;
    }

    /// <summary>
    /// Per-frame update. <paramref name="samples"/> may be null/empty when no
    /// live PCM is available; we then synthesize a believable spectrum.
    /// </summary>
    public void Update(float delta, float[]? samples, int sampleRate, bool active)
    {
        _time += delta;
        float[] target = new float[_bars.Length];

        if (active && samples != null && samples.Length >= 64 && sampleRate > 0)
        {
            ComputeGoertzel(samples, sampleRate, target);
        }
        else if (active)
        {
            ComputeFake(target);
        }
        // else: target stays all-zeros and bars decay.

        // Smooth bars towards target (attack faster than release for snappy feel).
        for (int i = 0; i < _bars.Length; i++)
        {
            float diff = target[i] - _bars[i];
            float rate = diff > 0 ? 22f : 7f;
            _bars[i] += diff * MathF.Min(1, delta * rate);
            if (_bars[i] < 0) _bars[i] = 0;

            if (_bars[i] > _peaks[i]) _peaks[i] = _bars[i];
            else _peaks[i] = MathF.Max(0, _peaks[i] - delta * 0.55f);
        }

        // Smooth global energy.
        float avg = 0;
        for (int i = 0; i < _bars.Length; i++) avg += _bars[i];
        avg /= _bars.Length;
        _energy += (avg - _energy) * MathF.Min(1, delta * 6f);
    }

    private void EnsureCoefs(int sampleRate)
    {
        if (sampleRate == _coefSampleRate) return;
        for (int i = 0; i < _bars.Length; i++)
            _coef[i] = 2f * MathF.Cos(2f * MathF.PI * _bandFreq[i] / sampleRate);
        _coefSampleRate = sampleRate;
    }

    private void ComputeGoertzel(float[] samples, int sampleRate, float[] target)
    {
        EnsureCoefs(sampleRate);
        int N = samples.Length;
        float invN = 1f / N;
        for (int b = 0; b < _bars.Length; b++)
        {
            float coef = _coef[b];
            float s1 = 0, s2 = 0;
            for (int i = 0; i < N; i++)
            {
                float s0 = samples[i] + coef * s1 - s2;
                s2 = s1; s1 = s0;
            }
            float mag2 = s1 * s1 + s2 * s2 - coef * s1 * s2;
            float mag = MathF.Sqrt(MathF.Max(0, mag2)) * invN;

            // Log-scale: log10(1 + mag*K) compresses the wide dynamic range
            // into something visualizer-friendly. Tune K so that "loud music"
            // saturates around ~1.0.
            float lev = MathF.Log10(1f + mag * 320f) * 1.35f;

            // A little extra weight on bass and a tilt up on the very top
            // bands, just to make kicks pop and hi-hats sparkle.
            float pos = b / (float)Math.Max(1, _bars.Length - 1);
            if (pos < 0.25f) lev *= 1.0f + (0.25f - pos) * 0.8f;
            if (pos > 0.85f) lev *= 1.0f + (pos - 0.85f) * 1.5f;

            target[b] = Math.Clamp(lev, 0f, 1f);
        }
    }

    private void ComputeFake(float[] target)
    {
        // Beat envelope re-arms every ~0.42 s with a little jitter.
        const float dt = 1f / 60f;  // approximate
        _beatTimer += dt;
        float period = 0.42f + 0.05f * MathF.Sin(_time * 0.3f);
        if (_beatTimer >= period)
        {
            _beatTimer -= period;
            _beatStrength = 1.0f;
        }
        _beatStrength = MathF.Max(0, _beatStrength - dt * 4f);

        for (int i = 0; i < _bars.Length; i++)
        {
            float band = (_bars.Length <= 1) ? 0f : (float)i / (_bars.Length - 1);
            float bass = (1 - MathF.Min(1, band * 1.6f)) * 0.6f * _beatStrength;
            float wobble = 0.10f
                + 0.18f * MathF.Sin(_time * (1.2f + i * 0.27f) + _phases[i])
                + 0.10f * MathF.Sin(_time * 3.1f + _phases[i] * 1.7f);
            wobble = MathF.Max(0, wobble);
            float spike = 0;
            if (band > 0.4f)
            {
                float n = 0.5f + 0.5f * MathF.Sin(i * 12.9898f + _time * 11.7f);
                if (n > 0.85f) spike = (n - 0.85f) * 4f * (0.4f + 0.6f * (1 - band));
            }
            target[i] = Math.Clamp(bass + wobble + spike, 0, 1);
        }
    }

    // Back-compat stub for older call sites — synthetic only.
    public void Tick(float delta, bool active) => Update(delta, null, 0, active);
}
