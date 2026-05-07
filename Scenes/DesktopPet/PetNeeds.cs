namespace MouseHouse.Scenes.DesktopPet;

/// <summary>
/// Soft, no-fail "tamagotchi" stats. Each need slowly decays from 100 toward
/// 0 while the pet is alive; interactions (eating cheese, sleeping in the
/// bed, running the wheel, sipping the bottle, getting petted) push them
/// back up. Nothing bad happens at 0 — the pet just expresses itself with
/// a thought bubble. The scene reads <see cref="LowestUnmet"/> to decide
/// whether to bias auto-routing toward a need-relevant toy.
/// </summary>
public class PetNeeds
{
    public float Hunger { get; private set; } = 80f;
    public float Energy { get; private set; } = 80f;
    public float Happy { get; private set; } = 80f;
    public float Hygiene { get; private set; } = 80f;

    // Per-second decay. Tuned so a fully-fed pet drifts to "hungry" over
    // ~3 minutes — slow enough to feel like ambient state, fast enough that
    // the user notices their toys / cheese matter.
    private const float HungerDecay = 0.45f;
    private const float EnergyDecay = 0.30f;
    private const float HappyDecay = 0.25f;
    private const float HygieneDecay = 0.18f;

    public bool ShowHud;

    public void Tick(float delta, bool sleeping)
    {
        // Decay all four needs. Sleeping counts as energy regen, not decay.
        Hunger = MathF.Max(0, Hunger - HungerDecay * delta);
        if (sleeping) Energy = MathF.Min(100, Energy + 6f * delta);
        else          Energy = MathF.Max(0, Energy - EnergyDecay * delta);
        Happy = MathF.Max(0, Happy - HappyDecay * delta);
        Hygiene = MathF.Max(0, Hygiene - HygieneDecay * delta);
    }

    public void Add(NeedKind kind, float amount)
    {
        switch (kind)
        {
            case NeedKind.Hunger: Hunger = Math.Clamp(Hunger + amount, 0, 100); break;
            case NeedKind.Energy: Energy = Math.Clamp(Energy + amount, 0, 100); break;
            case NeedKind.Happy: Happy = Math.Clamp(Happy + amount, 0, 100); break;
            case NeedKind.Hygiene: Hygiene = Math.Clamp(Hygiene + amount, 0, 100); break;
        }
    }

    public float Get(NeedKind kind) => kind switch
    {
        NeedKind.Hunger => Hunger,
        NeedKind.Energy => Energy,
        NeedKind.Happy => Happy,
        NeedKind.Hygiene => Hygiene,
        _ => 100,
    };

    /// <summary>
    /// Returns the most-unmet need below <paramref name="threshold"/>, or
    /// null if all four are above it. Caller uses this to bias auto-routing.
    /// </summary>
    public NeedKind? LowestUnmet(float threshold = 35f)
    {
        NeedKind? worst = null;
        float worstVal = threshold;
        if (Hunger < worstVal) { worst = NeedKind.Hunger; worstVal = Hunger; }
        if (Energy < worstVal) { worst = NeedKind.Energy; worstVal = Energy; }
        if (Happy < worstVal) { worst = NeedKind.Happy; worstVal = Happy; }
        if (Hygiene < worstVal) { worst = NeedKind.Hygiene; worstVal = Hygiene; }
        return worst;
    }
}

public enum NeedKind { Hunger, Energy, Happy, Hygiene }
