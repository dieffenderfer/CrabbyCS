using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.DesktopPet.Reactions;

/// <summary>
/// Tiny floating effect manager for pet interactions — hearts when the user
/// pets him, "achoo!" stars when his snoot gets booped, etc. Each effect is
/// a glyph + colour that drifts upward over a short lifetime, fading and
/// scaling slightly. Cheap enough to fire dozens at once.
/// </summary>
public class PetReactions
{
    private class Effect
    {
        public Vector2 Pos;
        public Vector2 Vel;
        public float Age;
        public float Lifetime;
        public string Glyph = "";
        public Color Color;
        public int Size;
    }

    private readonly List<Effect> _effects = new();

    public void Update(float delta)
    {
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            var e = _effects[i];
            e.Age += delta;
            e.Pos += e.Vel * delta;
            // Slight drag on the upward drift so they don't fly off too fast.
            e.Vel *= MathF.Max(0f, 1f - delta * 0.6f);
            if (e.Age >= e.Lifetime) _effects.RemoveAt(i);
        }
    }

    public void Draw()
    {
        foreach (var e in _effects)
        {
            float t = e.Age / e.Lifetime;             // 0..1
            float alpha = 1f - t * t;                 // ease-out fade
            float scale = 1f + t * 0.4f;              // grow slightly while rising
            int size = (int)(e.Size * scale);
            var c = new Color(e.Color.R, e.Color.G, e.Color.B, (byte)(alpha * 255));
            FontManager.DrawText(e.Glyph, (int)e.Pos.X, (int)e.Pos.Y, size, c);
        }
    }

    public void SpawnHeart(Vector2 origin)
    {
        _effects.Add(new Effect
        {
            Pos = origin,
            Vel = new Vector2((Rng() - 0.5f) * 14f, -32f),
            Lifetime = 1.2f,
            Glyph = "♥",
            Color = new Color((byte)240, (byte)80, (byte)110, (byte)255),
            Size = 16,
        });
    }

    public void SpawnSneeze(Vector2 origin, bool facingRight)
    {
        // "achoo!" text bubble that drifts in the direction the pet is facing,
        // plus a couple of star-puffs from the same point.
        float dx = facingRight ? 26f : -26f;
        _effects.Add(new Effect
        {
            Pos = origin + new Vector2(dx, -2f),
            Vel = new Vector2(dx * 1.5f, -18f),
            Lifetime = 0.9f,
            Glyph = "achoo!",
            Color = new Color((byte)40, (byte)40, (byte)50, (byte)255),
            Size = 12,
        });
        for (int i = 0; i < 3; i++)
        {
            _effects.Add(new Effect
            {
                Pos = origin + new Vector2(dx * 0.3f * i, -i * 4f),
                Vel = new Vector2(dx * (1.0f + i * 0.4f), -22f - i * 4f),
                Lifetime = 0.6f + i * 0.1f,
                Glyph = "*",
                Color = new Color((byte)200, (byte)200, (byte)220, (byte)255),
                Size = 14,
            });
        }
    }

    public void SpawnNote(Vector2 origin)
    {
        _effects.Add(new Effect
        {
            Pos = origin + new Vector2((Rng() - 0.5f) * 8f, 0),
            Vel = new Vector2((Rng() - 0.5f) * 18f, -28f),
            Lifetime = 1.4f,
            Glyph = "♪",
            Color = new Color((byte)120, (byte)180, (byte)240, (byte)255),
            Size = 16,
        });
    }

    public void Clear() => _effects.Clear();

    private static readonly Random _rng = new();
    private static float Rng() => (float)_rng.NextDouble();
}
