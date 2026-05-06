using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.DesktopPet.Cheese;

public class CheeseInstance
{
    public CheeseType Type;
    public Vector2 Position;
    public float Size = 1.0f;       // 1.0 fresh, 0 fully eaten
    public bool BeingEaten;
    public float WobblePhase;
    public float DroppedAtSeconds;
    // Crumbs accumulate as a piece is eaten.
    public int CrumbsSpawned;
}

public class Crumb
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Age;
    public float Life;
    public Color Color;
    public float Size;
}

public enum ReactionKind { Chomp, Nibble, Sleepy, Warm, Savor, Cool, Stink, Love, Salty, Spicy, Swirl, Peel, Feast }

public class Reaction
{
    public ReactionKind Kind;
    public Vector2 Anchor;     // pet position when triggered
    public float Age;
    public float Life;
    public CheeseType FromType;
}

/// <summary>
/// Owns all cheeses placed on screen, the crumbs they leave behind, and the
/// reaction effects that play after the pet finishes eating one. Tracks
/// session stats (count + favorite variety) for the small floating HUD.
/// </summary>
public class CheeseManager
{
    public readonly List<CheeseInstance> Active = new();
    public readonly List<Crumb> Crumbs = new();
    public readonly List<Reaction> Reactions = new();
    public int TotalEaten { get; private set; }
    private readonly int[] _eatenByType = new int[Enum.GetValues<CheeseType>().Length];

    private readonly Random _rng = new();
    private float _time;
    // Track recent drops for the "feast" easter egg: 5+ within 4 seconds → feast reaction.
    private readonly List<float> _recentDropTimes = new();

    // Subscribed by DesktopPetScene to get a feast reaction whenever ≥5 drops
    // pile up in a short window.
    public Action<CheeseType, Vector2>? OnFeastTriggered;

    public CheeseType? FavoriteType
    {
        get
        {
            int best = -1;
            int bestCount = 0;
            for (int i = 0; i < _eatenByType.Length; i++)
                if (_eatenByType[i] > bestCount) { best = i; bestCount = _eatenByType[i]; }
            return best < 0 ? null : (CheeseType)best;
        }
    }

    public int EatenOf(CheeseType t) => _eatenByType[(int)t];

    public void Drop(CheeseType type, Vector2 pos)
    {
        Active.Add(new CheeseInstance
        {
            Type = type,
            Position = pos,
            Size = 1.0f,
            WobblePhase = (float)_rng.NextDouble() * MathF.PI * 2,
            DroppedAtSeconds = _time,
        });
        // Track for feast detection.
        _recentDropTimes.Add(_time);
        _recentDropTimes.RemoveAll(t => _time - t > 4.0f);
        if (_recentDropTimes.Count >= 5)
        {
            // Fire once per crossing the threshold; clear the window so we
            // don't spam.
            _recentDropTimes.Clear();
            OnFeastTriggered?.Invoke(type, pos);
        }
    }

    public CheeseInstance? FindClosestUnclaimed(Vector2 petPos)
    {
        CheeseInstance? best = null;
        float bestD = float.MaxValue;
        foreach (var c in Active)
        {
            if (c.BeingEaten) continue;
            float d = Vector2.DistanceSquared(petPos, c.Position);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    public void Update(float delta)
    {
        _time += delta;

        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var c = Active[i];
            if (c.Size <= 0.01f) { Active.RemoveAt(i); continue; }
        }

        for (int i = Crumbs.Count - 1; i >= 0; i--)
        {
            var c = Crumbs[i];
            c.Age += delta;
            c.Position += c.Velocity * delta;
            c.Velocity *= 0.93f;
            if (c.Age >= c.Life) Crumbs.RemoveAt(i);
        }

        for (int i = Reactions.Count - 1; i >= 0; i--)
        {
            var r = Reactions[i];
            r.Age += delta;
            if (r.Age >= r.Life) Reactions.RemoveAt(i);
        }
    }

    public void OnBiteTaken(CheeseInstance c, Vector2 petCenter)
    {
        // Spawn a couple of crumbs flying away from the cheese.
        int want = (int)((1f - c.Size) * 12);
        while (c.CrumbsSpawned < want)
        {
            c.CrumbsSpawned++;
            float angle = (float)_rng.NextDouble() * MathF.PI * 2;
            float speed = 30 + (float)_rng.NextDouble() * 60;
            Crumbs.Add(new Crumb
            {
                Position = c.Position + new Vector2((float)(_rng.NextDouble() - 0.5) * 8,
                                                    (float)(_rng.NextDouble() - 0.5) * 8),
                Velocity = new Vector2(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed),
                Age = 0,
                Life = 0.7f + (float)_rng.NextDouble() * 0.5f,
                Size = 1f + (float)_rng.NextDouble() * 1.5f,
                Color = CrumbColor(c.Type),
            });
        }
    }

    public void RecordEat(CheeseInstance c, Vector2 petCenter)
    {
        TotalEaten++;
        _eatenByType[(int)c.Type]++;
        var def = Cheeses.Get(c.Type);
        Reactions.Add(new Reaction
        {
            Kind = MapReaction(def.Reaction),
            Anchor = petCenter,
            Age = 0,
            Life = def.Reaction == "sleepy" ? 2.5f : 1.8f,
            FromType = c.Type,
        });
    }

    public void TriggerFeast(Vector2 petCenter)
    {
        Reactions.Add(new Reaction
        {
            Kind = ReactionKind.Feast,
            Anchor = petCenter,
            Age = 0,
            Life = 2.4f,
        });
    }

    private static ReactionKind MapReaction(string tag) => tag switch
    {
        "chomp" => ReactionKind.Chomp,
        "nibble" => ReactionKind.Nibble,
        "sleepy" => ReactionKind.Sleepy,
        "warm" => ReactionKind.Warm,
        "savor" => ReactionKind.Savor,
        "cool" => ReactionKind.Cool,
        "stink" => ReactionKind.Stink,
        "love" => ReactionKind.Love,
        "salty" => ReactionKind.Salty,
        "spicy" => ReactionKind.Spicy,
        "swirl" => ReactionKind.Swirl,
        "peel" => ReactionKind.Peel,
        _ => ReactionKind.Chomp,
    };

    private static Color CrumbColor(CheeseType t) => t switch
    {
        CheeseType.Cheddar => new Color((byte)244, (byte)168, (byte)60, (byte)255),
        CheeseType.Swiss => new Color((byte)248, (byte)232, (byte)132, (byte)255),
        CheeseType.Brie => new Color((byte)248, (byte)240, (byte)200, (byte)255),
        CheeseType.Gouda => new Color((byte)238, (byte)200, (byte)110, (byte)255),
        CheeseType.Parmesan => new Color((byte)238, (byte)220, (byte)150, (byte)255),
        CheeseType.Mozzarella => new Color((byte)252, (byte)250, (byte)244, (byte)255),
        CheeseType.Blue => new Color((byte)200, (byte)200, (byte)190, (byte)255),
        CheeseType.Camembert => new Color((byte)252, (byte)244, (byte)220, (byte)255),
        CheeseType.Feta => new Color((byte)252, (byte)252, (byte)252, (byte)255),
        CheeseType.PepperJack => new Color((byte)244, (byte)232, (byte)180, (byte)255),
        CheeseType.ColbyJack => new Color((byte)240, (byte)190, (byte)100, (byte)255),
        CheeseType.StringCheese => new Color((byte)252, (byte)250, (byte)240, (byte)255),
        _ => new Color((byte)240, (byte)200, (byte)100, (byte)255),
    };

    public void Draw()
    {
        // Crumbs underneath the cheeses
        foreach (var c in Crumbs)
        {
            float t = 1f - c.Age / c.Life;
            byte alpha = (byte)Math.Clamp((int)(t * 255), 0, 255);
            Raylib.DrawCircleV(c.Position, c.Size * t, new Color(c.Color.R, c.Color.G, c.Color.B, alpha));
        }

        // Cheese sprites
        foreach (var c in Active)
        {
            float wobble = MathF.Sin(_time * 4 + c.WobblePhase) * 0.04f;
            float scale = (0.9f + wobble) * c.Size;
            byte alpha = (byte)Math.Clamp((int)(c.Size * 255), 0, 255);
            // Soft shadow disc
            Raylib.DrawEllipse((int)c.Position.X, (int)c.Position.Y + (int)(8 * scale),
                14 * scale, 4 * scale,
                new Color((byte)0, (byte)0, (byte)0, (byte)Math.Min(80, (int)(alpha / 3))));
            CheeseSprites.Draw(c.Type, c.Position, scale * 1.2f, alpha);
        }

        // Reactions overlay (above everything).
        foreach (var r in Reactions) DrawReaction(r);
    }

    private void DrawReaction(Reaction r)
    {
        float t = r.Age / r.Life;
        float fade = 1f - t;
        byte alpha = (byte)Math.Clamp((int)(fade * 255), 0, 255);
        Vector2 pos = r.Anchor + new Vector2(0, -50 - t * 24);  // float up
        switch (r.Kind)
        {
            case ReactionKind.Chomp:
                Glyph("nom!", pos, new Color((byte)255, (byte)200, (byte)80, alpha)); break;
            case ReactionKind.Nibble:
                Glyph("nibble", pos, new Color((byte)220, (byte)200, (byte)100, alpha)); break;
            case ReactionKind.Sleepy:
                // ZZZ stack
                for (int i = 0; i < 3; i++)
                    Glyph("z", pos + new Vector2(i * 6, -i * 8 - MathF.Sin(t * 6 + i) * 2),
                        new Color((byte)180, (byte)200, (byte)240, alpha), 16 - i * 2);
                break;
            case ReactionKind.Warm:
                // Soft yellow glow halo
                Raylib.DrawCircleV(r.Anchor + new Vector2(0, -20), 30 * fade,
                    new Color((byte)255, (byte)220, (byte)120, (byte)Math.Min((byte)120, alpha)));
                Glyph("mmm", pos, new Color((byte)255, (byte)220, (byte)120, alpha)); break;
            case ReactionKind.Savor:
                // Music notes drifting up
                for (int i = 0; i < 3; i++)
                {
                    float phase = t * 6 + i;
                    Glyph("♪", pos + new Vector2(MathF.Sin(phase) * 10 + i * 6, -i * 10),
                        new Color((byte)255, (byte)240, (byte)200, alpha), 18);
                }
                break;
            case ReactionKind.Cool:
                Glyph("～", pos, new Color((byte)180, (byte)220, (byte)255, alpha), 18); break;
            case ReactionKind.Stink:
                // Wavy green stink lines + face glyph
                for (int i = 0; i < 3; i++)
                {
                    float x = (i - 1) * 8 + MathF.Sin(t * 8 + i) * 4;
                    Raylib.DrawCircleV(pos + new Vector2(x, -t * 8),
                        3 * fade, new Color((byte)140, (byte)200, (byte)100, (byte)Math.Min((byte)150, alpha)));
                }
                Glyph(">~<", pos + new Vector2(-2, 6),
                    new Color((byte)140, (byte)200, (byte)100, alpha), 16);
                break;
            case ReactionKind.Love:
                // Pink hearts — three drifting up
                for (int i = 0; i < 3; i++)
                {
                    float phase = t * 4 + i;
                    Glyph("♥", pos + new Vector2(MathF.Sin(phase) * 12 + i * 4 - 6, -i * 8),
                        new Color((byte)255, (byte)160, (byte)200, alpha), 18);
                }
                break;
            case ReactionKind.Salty:
                // Tiny white specks falling
                for (int i = 0; i < 6; i++)
                {
                    float ang = i * 0.7f;
                    Raylib.DrawCircleV(pos + new Vector2(MathF.Cos(ang) * 10,
                                                         MathF.Sin(ang) * 6 + t * 8),
                        2 * fade, new Color((byte)255, (byte)255, (byte)255, alpha));
                }
                Glyph("salty!", pos + new Vector2(0, -12),
                    new Color((byte)200, (byte)220, (byte)255, alpha), 14);
                break;
            case ReactionKind.Spicy:
                // Steam / flame flicker
                for (int i = 0; i < 5; i++)
                {
                    float x = (i - 2) * 5;
                    Raylib.DrawCircleV(pos + new Vector2(x, -t * 16),
                        3 * fade, new Color((byte)255, (byte)120, (byte)40, (byte)Math.Min((byte)180, alpha)));
                }
                Glyph("HOT!", pos + new Vector2(0, -8),
                    new Color((byte)255, (byte)80, (byte)40, alpha)); break;
            case ReactionKind.Swirl:
                // Two-tone swirl
                for (int i = 0; i < 8; i++)
                {
                    float phase = t * 8 + i * 0.7f;
                    Raylib.DrawCircleV(pos + new Vector2(MathF.Cos(phase) * 14, MathF.Sin(phase) * 8),
                        2.5f * fade,
                        i % 2 == 0
                            ? new Color((byte)255, (byte)200, (byte)80, alpha)
                            : new Color((byte)255, (byte)240, (byte)220, alpha));
                }
                break;
            case ReactionKind.Peel:
                // String strands flying off
                for (int i = 0; i < 4; i++)
                {
                    float ang = i * 0.5f - 0.75f;
                    var p = pos + new Vector2(MathF.Cos(ang) * 14 + i * 3,
                                              MathF.Sin(ang) * 8 - 4);
                    Raylib.DrawLineEx(p, p + new Vector2(0, 8), 1.5f * fade,
                        new Color((byte)252, (byte)250, (byte)240, alpha));
                }
                Glyph("peel!", pos + new Vector2(0, -10),
                    new Color((byte)252, (byte)250, (byte)240, alpha), 14);
                break;
            case ReactionKind.Feast:
                // Sparkles + big "FEAST!" text
                for (int i = 0; i < 12; i++)
                {
                    float ang = i / 12f * MathF.PI * 2 + t * 4;
                    float radius = 10 + t * 30;
                    var p = r.Anchor + new Vector2(MathF.Cos(ang) * radius, -20 + MathF.Sin(ang) * radius);
                    Raylib.DrawCircleV(p, 2 + 1.5f * fade,
                        new Color((byte)255, (byte)220, (byte)120, alpha));
                }
                Glyph("FEAST!", r.Anchor + new Vector2(-20, -60 - t * 20),
                    new Color((byte)255, (byte)160, (byte)80, alpha), 22);
                break;
        }
    }

    private static void Glyph(string s, Vector2 pos, Color col, int size = 16)
    {
        // Draw a small black drop-shadow then the glyph itself; FontManager
        // handles the W95F → fallback pipeline so non-ASCII glyphs (♥ ♪ ～)
        // render cleanly.
        FontManager.DrawText(s, (int)pos.X + 1, (int)pos.Y + 1,
            size, new Color((byte)0, (byte)0, (byte)0, (byte)Math.Min((byte)140, col.A)));
        FontManager.DrawText(s, (int)pos.X, (int)pos.Y, size, col);
    }
}
