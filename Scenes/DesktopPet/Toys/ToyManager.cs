using System.Numerics;
using System.Text.Json;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.DesktopPet.Toys;

public class ToyInstance
{
    public ToyType Type { get; set; }
    /// <summary>Top-left of the toy's bounding rect, in screen pixels.</summary>
    public Vector2 Position { get; set; }
    public bool InUse;
    public float UseTimer;        // accumulates while pet is interacting
    public float WheelAngle;      // wheel-only — radians, advances during use
    public Vector2 BallVel;       // ball-only — drift velocity, decays
    public float WaterDrip;       // water-only — 0..1 oscillator for drip animation

    public Vector2 InteractPoint() => Position + Toys.Get(Type).InteractAnchor;
    public Rectangle Bounds() => new(Position.X, Position.Y,
        Toys.Get(Type).Width, Toys.Get(Type).Height);
}

/// <summary>
/// Owns the placed toys, drives their per-frame animation/physics, and
/// serialises them to disk so they survive app restarts. Pet ↔ toy
/// interaction policy lives in <c>DesktopPetScene</c>; the manager just
/// answers spatial questions ("nearest toy?") and renders.
/// </summary>
public class ToyManager
{
    public readonly List<ToyInstance> Toys = new();
    private readonly Random _rng = new();
    private float _time;

    private static string SavePath
        => Path.Combine(SaveManager.SaveDirectory, "toys.json");

    public ToyInstance Place(ToyType type, Vector2 pos)
    {
        var def = Scenes.DesktopPet.Toys.Toys.Get(type);
        // Anchor the toy by its center so the click feels right.
        var topLeft = pos - new Vector2(def.Width / 2f, def.Height / 2f);
        // Limits: keep within screen bounds (caller should pass screen size if it cares).
        var t = new ToyInstance { Type = type, Position = topLeft };
        Toys.Add(t);
        Save();
        return t;
    }

    public void Remove(ToyInstance t)
    {
        Toys.Remove(t);
        Save();
    }

    public void Clear()
    {
        Toys.Clear();
        Save();
    }

    public ToyInstance? FindClosestUnused(Vector2 from, ToyType? prefer = null)
    {
        ToyInstance? best = null;
        float bestD = float.MaxValue;
        foreach (var t in Toys)
        {
            if (t.InUse) continue;
            if (prefer.HasValue && t.Type != prefer.Value) continue;
            float d = Vector2.DistanceSquared(from, t.InteractPoint());
            if (d < bestD) { bestD = d; best = t; }
        }
        // No toy of preferred type? Fall back to any unused.
        if (best == null && prefer.HasValue) return FindClosestUnused(from, null);
        return best;
    }

    public ToyInstance? HitTest(Vector2 p)
    {
        // Last-placed wins (drawn on top).
        for (int i = Toys.Count - 1; i >= 0; i--)
        {
            var t = Toys[i];
            if (Raylib.CheckCollisionPointRec(p, t.Bounds())) return t;
        }
        return null;
    }

    public void Update(float delta)
    {
        _time += delta;
        foreach (var t in Toys)
        {
            switch (t.Type)
            {
                case ToyType.Wheel:
                    if (t.InUse) t.WheelAngle += delta * 8f;       // run
                    else         t.WheelAngle += delta * 0.4f;     // gentle idle drift
                    break;
                case ToyType.WaterBottle:
                    t.WaterDrip = (MathF.Sin(_time * 0.8f + (int)t.Position.X * 0.01f) + 1f) * 0.5f;
                    break;
                case ToyType.Ball:
                    // Inertial drift; a "kick" sets BallVel via API call below.
                    t.Position += t.BallVel * delta;
                    t.BallVel *= MathF.Max(0f, 1f - delta * 1.4f);
                    if (t.BallVel.LengthSquared() < 1f) t.BallVel = Vector2.Zero;
                    break;
            }
        }
    }

    /// <summary>Apply an impulse to the ball under the given toy.</summary>
    public void KickBall(ToyInstance ball, Vector2 dir, float strength)
    {
        if (ball.Type != ToyType.Ball) return;
        ball.BallVel = Vector2.Normalize(dir) * strength;
    }

    public void Draw()
    {
        foreach (var t in Toys)
        {
            var d = Scenes.DesktopPet.Toys.Toys.Get(t.Type);
            switch (t.Type)
            {
                case ToyType.Bed:
                    ToySprites.DrawBed(t.Position, d.Width, d.Height);
                    break;
                case ToyType.Wheel:
                    ToySprites.DrawWheel(t.Position, d.Width, d.Height, t.WheelAngle);
                    break;
                case ToyType.WaterBottle:
                    ToySprites.DrawWaterBottle(t.Position, d.Width, d.Height, t.WaterDrip);
                    break;
                case ToyType.Ball:
                    var center = t.Position + new Vector2(d.Width / 2f, d.Height / 2f);
                    float spin = MathF.Atan2(t.BallVel.Y, t.BallVel.X)
                                 + (t.BallVel.LengthSquared() > 1f ? _time * 6f : 0f);
                    ToySprites.DrawBall(center, d.Width / 2f, spin);
                    break;
            }
        }
    }

    // ── Persistence ──────────────────────────────────────────────────────

    public void Load()
    {
        Toys.Clear();
        try
        {
            if (!File.Exists(SavePath)) return;
            var json = File.ReadAllText(SavePath);
            var raw = JsonSerializer.Deserialize<List<SerToy>>(json);
            if (raw == null) return;
            foreach (var s in raw)
            {
                if (!Enum.TryParse<ToyType>(s.Type, out var type)) continue;
                Toys.Add(new ToyInstance { Type = type, Position = new Vector2(s.X, s.Y) });
            }
        }
        catch { /* corrupt file → start fresh */ }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SaveManager.SaveDirectory);
            var raw = Toys.Select(t => new SerToy
            {
                Type = t.Type.ToString(),
                X = t.Position.X,
                Y = t.Position.Y,
            }).ToList();
            var json = JsonSerializer.Serialize(raw,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SavePath, json);
        }
        catch { /* save errors aren't fatal — the desktop pet keeps working */ }
    }

    private class SerToy
    {
        public string Type { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
    }
}
