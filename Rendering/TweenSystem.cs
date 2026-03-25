namespace MouseHouse.Rendering;

public enum EaseType
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut
}

public class Tween
{
    private readonly List<TweenStep> _steps = new();
    private int _currentStep;
    private bool _finished;
    public bool Finished => _finished;

    public Tween TweenValue(Action<float> setter, float from, float to, float duration, EaseType ease = EaseType.Linear)
    {
        _steps.Add(new TweenStep(setter, from, to, duration, ease));
        return this;
    }

    public Tween Wait(float duration)
    {
        _steps.Add(new TweenStep(null, 0, 0, duration, EaseType.Linear));
        return this;
    }

    public void Update(float delta)
    {
        if (_finished || _currentStep >= _steps.Count) { _finished = true; return; }

        var step = _steps[_currentStep];
        step.Elapsed += delta;
        float t = Math.Clamp(step.Elapsed / step.Duration, 0f, 1f);
        float eased = ApplyEase(t, step.Ease);
        step.Setter?.Invoke(step.From + (step.To - step.From) * eased);

        if (step.Elapsed >= step.Duration)
        {
            step.Setter?.Invoke(step.To);
            _currentStep++;
            if (_currentStep >= _steps.Count) _finished = true;
        }
    }

    private static float ApplyEase(float t, EaseType ease) => ease switch
    {
        EaseType.EaseIn => t * t,
        EaseType.EaseOut => 1f - (1f - t) * (1f - t),
        EaseType.EaseInOut => t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2f) / 2f,
        _ => t
    };

    private class TweenStep
    {
        public Action<float>? Setter;
        public float From, To, Duration, Elapsed;
        public EaseType Ease;
        public TweenStep(Action<float>? setter, float from, float to, float duration, EaseType ease)
        {
            Setter = setter; From = from; To = to; Duration = duration; Ease = ease;
        }
    }
}

/// <summary>
/// Global tween manager. Create tweens via TweenSystem.Create(), they auto-update.
/// </summary>
public static class TweenSystem
{
    private static readonly List<Tween> _tweens = new();

    public static Tween Create()
    {
        var t = new Tween();
        _tweens.Add(t);
        return t;
    }

    public static void Update(float delta)
    {
        for (int i = _tweens.Count - 1; i >= 0; i--)
        {
            _tweens[i].Update(delta);
            if (_tweens[i].Finished)
                _tweens.RemoveAt(i);
        }
    }

    public static void Clear() => _tweens.Clear();
}
