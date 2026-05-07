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
    // Per-cheese permutation of pixel cells used by the dissolve effect —
    // populated when the cheese is dropped so each piece has its own
    // disappearing-pixel pattern.
    public int[] DissolveOrder = Array.Empty<int>();
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

/// <summary>
/// Owns all cheeses placed on screen and the crumbs they leave behind.
/// Tracks total eaten so the AI can keep picking up dropped pieces.
/// </summary>
public class CheeseManager
{
    public readonly List<CheeseInstance> Active = new();
    public readonly List<Crumb> Crumbs = new();
    public int TotalEaten { get; private set; }

    private readonly Random _rng = new();
    private float _time;

    public void Drop(CheeseType type, Vector2 pos)
    {
        // Build a per-cheese pixel-dissolve permutation. Each filled cell of
        // the cheddar grid gets a unique slot; eating shrinks Size and the
        // renderer skips the first N cells of this order.
        int n = CheeseSprites.CheddarCellCount;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        for (int i = n - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        Active.Add(new CheeseInstance
        {
            Type = type,
            Position = pos,
            Size = 1.0f,
            WobblePhase = (float)_rng.NextDouble() * MathF.PI * 2,
            DroppedAtSeconds = _time,
            DissolveOrder = order,
        });
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
    }

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
        // Crumbs underneath the cheeses — small solid pixels that fade by
        // shrinking, not by alpha, to keep the look hard-edged.
        foreach (var c in Crumbs)
        {
            float t = 1f - c.Age / c.Life;
            // Floor at 2 px so the particles always read as a chunky pixel
            // instead of a 1-px speck.
            int sz = Math.Max(2, (int)MathF.Round(c.Size * t));
            Raylib.DrawRectangle((int)c.Position.X - sz / 2, (int)c.Position.Y - sz / 2,
                sz, sz, c.Color);
        }

        // Cheese sprites — fixed scale, fully opaque. Eaten progress is
        // expressed as missing pixels via the dissolve permutation.
        foreach (var c in Active)
        {
            int hide = (int)MathF.Round((1f - c.Size) * c.DissolveOrder.Length);
            CheeseSprites.DrawCheddarDissolve(c.Position, 1.2f, c.DissolveOrder, hide);
        }
    }

}
