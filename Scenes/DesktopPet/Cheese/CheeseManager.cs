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
    // Order in which the sprite's opaque pixel indices vanish. Initially
    // a random permutation (a not-yet-claimed cheese has no preferred
    // direction); rebuilt as a directional sort when the pet first bites
    // so the contact side vanishes first and the far side last.
    public int[] DissolveOrder = Array.Empty<int>();
    // Per-pixel timestamp of when each opaque pixel transitioned to
    // hidden — drives the brief "fall under gravity" animation before
    // the pixel actually disappears. -1 means still visible.
    public float[] HideTimes = Array.Empty<float>();
    // Set true the first time OnBiteTaken runs for this instance; used
    // to gate the "compute eat direction + rebuild DissolveOrder" pass
    // so we only do it once per cheese.
    public bool EatDirectionSet;
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
        // Random placeholder permutation so the cheese can fully render
        // before a pet ever touches it (e.g. it's just sitting on the
        // desk). The order gets rebuilt directionally on the first bite
        // so pixels facing the pet vanish first.
        int n = CheeseImages.GetOpaquePixels(type).Length;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        for (int i = n - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        var hideTimes = new float[n];
        for (int i = 0; i < n; i++) hideTimes[i] = -1f;

        Active.Add(new CheeseInstance
        {
            Type = type,
            Position = pos,
            Size = 1.0f,
            WobblePhase = (float)_rng.NextDouble() * MathF.PI * 2,
            DroppedAtSeconds = _time,
            DissolveOrder = order,
            HideTimes = hideTimes,
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
            // Gravity + drag: pulls crumbs down so the "bits flicking
            // off the cheese" sit on the desktop briefly before fading.
            c.Velocity = new Vector2(c.Velocity.X * 0.94f,
                                     c.Velocity.Y * 0.94f + 380f * delta);
            if (c.Age >= c.Life) Crumbs.RemoveAt(i);
        }
    }

    public void OnBiteTaken(CheeseInstance c, Vector2 petCenter)
    {
        // First bite: rebuild DissolveOrder so pixels facing the pet
        // vanish first and the far side last. Sort by signed projection
        // of each pixel's offset-from-sprite-center onto the pet→cheese
        // direction — bigger projection = closer to pet's mouth side.
        // A small per-pixel jitter prevents pixels at the same projection
        // from popping off in a hard vertical line.
        if (!c.EatDirectionSet && c.DissolveOrder.Length > 0)
        {
            var pixels = CheeseImages.GetOpaquePixels(c.Type);
            var (sw, sh) = CheeseImages.GetSpriteSize(c.Type);
            var dir = petCenter - c.Position;
            float len = dir.Length();
            if (len > 0.001f) dir /= len;
            else dir = new Vector2(-1f, 0f);    // fallback: chew leftward

            float halfW = sw / 2f, halfH = sh / 2f;
            int n = pixels.Length;
            // Jitter scales with sprite size so the result reads as
            // "noisy chew", not "pixel-perfect wave". ~25% of the
            // half-extent perpendicular to the direction; sprite is
            // typically ~30 px so this lands at ±3-4 px of slop, which
            // mixes adjacent rows of the dissolution front instead of
            // peeling them off in clean order.
            float spriteExtent = MathF.Max(halfW, halfH);
            float jitterRange = spriteExtent * 0.50f;
            var keys = new float[n];
            for (int i = 0; i < n; i++)
            {
                float dx = pixels[i].X - halfW;
                float dy = pixels[i].Y - halfH;
                float proj = dx * dir.X + dy * dir.Y;
                float jitter = ((float)_rng.NextDouble() - 0.5f) * jitterRange;
                keys[i] = proj + jitter;
            }
            // Sort indices by key descending (largest projection — i.e.
            // closest to pet — first).
            var order = c.DissolveOrder;
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => keys[b].CompareTo(keys[a]));
            c.EatDirectionSet = true;
        }

        // Light crumb-spray off the bite site for added texture — the
        // falling-pixel animation in DrawDissolve already provides the
        // primary "pieces fall off" read, so keep this dialed back so
        // it doesn't double up.
        int want = (int)((1f - c.Size) * 6);
        while (c.CrumbsSpawned < want)
        {
            c.CrumbsSpawned++;
            // Bias crumbs toward the pet side and downward so they read
            // as bits flicked off the chewed edge, not a 360° explosion.
            var biteDir = (petCenter - c.Position);
            if (biteDir.LengthSquared() > 0.001f) biteDir = Vector2.Normalize(biteDir);
            else biteDir = new Vector2(-1f, 0f);
            float spread = ((float)_rng.NextDouble() - 0.5f) * 1.6f;
            float cs = MathF.Cos(spread), sn = MathF.Sin(spread);
            var dir = new Vector2(biteDir.X * cs - biteDir.Y * sn,
                                  biteDir.X * sn + biteDir.Y * cs);
            float speed = 40 + (float)_rng.NextDouble() * 70;
            Crumbs.Add(new Crumb
            {
                Position = c.Position + dir * 6f
                    + new Vector2((float)(_rng.NextDouble() - 0.5) * 4,
                                  (float)(_rng.NextDouble() - 0.5) * 4),
                Velocity = dir * speed + new Vector2(0, -20f),     // small upward kick before gravity
                Age = 0,
                Life = 0.4f + (float)_rng.NextDouble() * 0.3f,
                Size = 1f + (float)_rng.NextDouble() * 1.2f,
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

    /// <summary>
    /// Render every active cheese + flying crumb. <paramref name="cellPx"/>
    /// is the per-sprite-pixel block size, matched to the pet scale by the
    /// caller so the cheese (and the crumbs that fall off it) keep
    /// proportional chunkiness against the rendered mouse.
    /// </summary>
    public void Draw(int cellPx)
    {
        if (cellPx < 1) cellPx = 1;
        // Crumbs underneath the cheeses — small solid pixels that fade by
        // shrinking, not by alpha, to keep the look hard-edged. Floor
        // matches cellPx so a crumb is never smaller than a dissolved
        // cheese pixel; that mismatch was the visual bug ("dissolves to
        // single pixels but the crumbs are bigger than that").
        foreach (var c in Crumbs)
        {
            float t = 1f - c.Age / c.Life;
            int sz = Math.Max(cellPx, (int)MathF.Round(c.Size * cellPx * t));
            Raylib.DrawRectangle((int)c.Position.X - sz / 2, (int)c.Position.Y - sz / 2,
                sz, sz, c.Color);
        }

        // Textured cheese sprites — eaten progress walks DissolveOrder
        // from the pet-facing side. Pixels that have just been hidden
        // play a short fall-under-gravity animation in DrawDissolve
        // before they actually disappear, so the cheese visibly sheds
        // pieces from the chewed edge.
        foreach (var c in Active)
        {
            int hide = (int)MathF.Round((1f - c.Size) * c.DissolveOrder.Length);
            CheeseImages.DrawDissolve(c.Type, c.Position, c.DissolveOrder,
                hide, cellPx, c.HideTimes, _time);
        }
    }

}
