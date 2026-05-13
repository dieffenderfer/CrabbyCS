using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Fish aquarium "screensaver" — a procedural school of fish lazily
/// crossing the screen. Two background modes:
///
///   • Aquarium:   fills the screen with a dark teal so the fish sit in a
///                 traditional black-aquarium look.
///   • Transparent: skips the background fill, letting the fish swim
///                 directly across the user's desktop through the app's
///                 transparent overlay.
///
/// No assets — fish are drawn from primitives (ellipse body, triangle
/// tail, eye dot) so the screensaver is asset-free and reskins cleanly
/// at any resolution. Click anywhere or press Esc to close.
/// </summary>
public class FishAquariumActivity : IActivity
{
    public Vector2 PanelSize { get; private set; }
    public bool IsFinished { get; private set; }

    public bool TransparentBackground => true;

    // Empty-space clicks DON'T close — only clicks on the small hint /
    // toggle band at the bottom. Keeps the pet usable behind the
    // transparent variant without forcing the user to dodge it.
    public bool ContainsPoint(Vector2 panelLocalPos)
    {
        // Only the bottom hint strip + ESC consumes input. Clicks elsewhere
        // pass through to the pet (when in transparent mode).
        return panelLocalPos.Y >= PanelSize.Y - 28;
    }

    private enum BgMode { Aquarium, Transparent }
    private BgMode _bg = BgMode.Aquarium;

    private struct Fish
    {
        public Vector2 Pos;
        public float Speed;          // px/sec — sign sets facing
        public float Size;           // body half-length, px
        public float BobPhase;
        public float BobFreq;
        public Color Body;
        public Color Tail;
    }

    private readonly List<Fish> _fish = new();
    private readonly Random _rng = new();
    private float _time;

    private static readonly (Color Body, Color Tail)[] FishPalette =
    {
        (new(244, 140,  60, 255), new(208,  92,  20, 255)),  // koi orange
        (new(220, 220, 230, 255), new(160, 170, 200, 255)),  // moonlight
        (new(110, 184, 110, 255), new( 70, 132,  72, 255)),  // tetra green
        (new(232, 196,  80, 255), new(204, 144,  44, 255)),  // gold
        (new(120, 168, 232, 255), new( 80, 124, 196, 255)),  // bluefin
        (new(220, 120, 168, 255), new(176,  84, 128, 255)),  // angelfish
    };

    public FishAquariumActivity()
    {
        PanelSize = new Vector2(
            (int)(Raylib.GetScreenWidth() / UIScaling.Factor),
            (int)(Raylib.GetScreenHeight() / UIScaling.Factor));
    }

    public void Load()
    {
        _fish.Clear();
        int n = 10;
        for (int i = 0; i < n; i++) _fish.Add(SpawnFish(initial: true));
    }

    private Fish SpawnFish(bool initial)
    {
        float size = 14f + (float)_rng.NextDouble() * 22f;
        float speed = (30f + (float)_rng.NextDouble() * 50f) * (_rng.Next(2) == 0 ? -1 : 1);
        // initial=true scatters fish across the screen so the first frame
        // isn't a stampede entering from the edges. After that we spawn
        // just-off-screen on the entry side so swimming reads as inbound.
        float x = initial
            ? (float)_rng.NextDouble() * PanelSize.X
            : (speed > 0 ? -size * 2 : PanelSize.X + size * 2);
        float y = 40 + (float)_rng.NextDouble() * (PanelSize.Y - 80);
        var palette = FishPalette[_rng.Next(FishPalette.Length)];
        return new Fish
        {
            Pos = new Vector2(x, y),
            Speed = speed,
            Size = size,
            BobPhase = (float)(_rng.NextDouble() * Math.PI * 2),
            BobFreq = 1.6f + (float)_rng.NextDouble() * 1.4f,
            Body = palette.Body,
            Tail = palette.Tail,
        };
    }

    public void Close() => IsFinished = true;

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        _time += delta;

        // ESC closes the screensaver. (Activity panel ESC is caught above
        // us in DesktopPetScene, but in case the focus path changes we
        // double-up here.)
        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { IsFinished = true; return; }

        var local = mousePos - panelOffset;

        // Bottom strip = control band. Click on the left button toggles
        // the background mode; right button closes.
        var bgBtn = BgButtonRect();
        var closeBtn = CloseButtonRect();
        if (leftPressed)
        {
            if (PointIn(local, bgBtn))
            {
                _bg = _bg == BgMode.Aquarium ? BgMode.Transparent : BgMode.Aquarium;
                return;
            }
            if (PointIn(local, closeBtn))
            {
                IsFinished = true;
                return;
            }
        }

        // Swim — sinusoidal vertical bob layered onto horizontal drift.
        for (int i = 0; i < _fish.Count; i++)
        {
            var f = _fish[i];
            f.Pos.X += f.Speed * delta;
            f.Pos.Y += MathF.Sin(_time * f.BobFreq + f.BobPhase) * delta * 6f;
            if ((f.Speed > 0 && f.Pos.X > PanelSize.X + f.Size * 2)
                || (f.Speed < 0 && f.Pos.X < -f.Size * 2))
            {
                _fish[i] = SpawnFish(initial: false);
            }
            else
            {
                _fish[i] = f;
            }
        }
    }

    private static bool PointIn(Vector2 p, Rectangle r) =>
        p.X >= r.X && p.X <= r.X + r.Width && p.Y >= r.Y && p.Y <= r.Y + r.Height;

    private Rectangle BgButtonRect()
        => new(12, PanelSize.Y - 24, 160, 20);

    private Rectangle CloseButtonRect()
        => new(PanelSize.X - 90, PanelSize.Y - 24, 78, 20);

    public void Draw(Vector2 panelOffset)
    {
        // Background.
        if (_bg == BgMode.Aquarium)
        {
            Raylib.DrawRectangle((int)panelOffset.X, (int)panelOffset.Y,
                (int)PanelSize.X, (int)PanelSize.Y,
                new Color((byte)8, (byte)22, (byte)38, (byte)230));
            // Subtle vertical gradient hint — bands of slightly lighter blue.
            for (int b = 0; b < 6; b++)
            {
                int by = (int)(panelOffset.Y + (PanelSize.Y / 6f) * b);
                int bh = (int)(PanelSize.Y / 6f) + 1;
                Raylib.DrawRectangle((int)panelOffset.X, by,
                    (int)PanelSize.X, bh,
                    new Color((byte)10, (byte)28 + b * 3, (byte)44 + b * 4, (byte)20));
            }
        }

        // Fish.
        foreach (var f in _fish) DrawFish(panelOffset, f);

        // Control band — small chrome at the bottom so the user knows how
        // to toggle the background or exit.
        var bg = BgButtonRect();
        var cb = CloseButtonRect();
        DrawButton(panelOffset, bg, _bg == BgMode.Aquarium ? "Background: Aquarium" : "Background: Transparent");
        DrawButton(panelOffset, cb, "Close (Esc)");
    }

    private static void DrawButton(Vector2 panelOffset, Rectangle r, string label)
    {
        var abs = new Rectangle(panelOffset.X + r.X, panelOffset.Y + r.Y, r.Width, r.Height);
        Raylib.DrawRectangleRec(abs, new Color((byte)0, (byte)0, (byte)0, (byte)140));
        Raylib.DrawRectangleLinesEx(abs, 1, new Color((byte)220, (byte)220, (byte)230, (byte)200));
        int tw = FontManager.MeasureText(label, 12);
        FontManager.DrawText(label,
            (int)(abs.X + (abs.Width - tw) / 2),
            (int)(abs.Y + 4),
            12, new Color((byte)230, (byte)230, (byte)240, (byte)255));
    }

    private static void DrawFish(Vector2 panelOffset, Fish f)
    {
        bool facingRight = f.Speed > 0;
        float dirSign = facingRight ? 1f : -1f;
        var origin = panelOffset + f.Pos;

        // Body: filled ellipse approximated via DrawEllipse.
        Raylib.DrawEllipse((int)origin.X, (int)origin.Y,
            f.Size, f.Size * 0.55f, f.Body);

        // Tail: animated triangle swishing in counter-phase with the body bob.
        float swish = MathF.Sin((origin.X + origin.Y) * 0.04f) * 0.35f;
        float tailRoot = origin.X - dirSign * f.Size * 0.9f;
        var tailTip = new Vector2(
            origin.X - dirSign * (f.Size * 1.55f),
            origin.Y + swish * f.Size * 0.5f);
        var tailUp = new Vector2(tailRoot, origin.Y - f.Size * 0.45f);
        var tailDn = new Vector2(tailRoot, origin.Y + f.Size * 0.45f);
        // Raylib requires CCW winding for a filled triangle. Reverse two
        // verts when the fish faces left so the fill remains visible
        // instead of culling.
        if (facingRight)
            Raylib.DrawTriangle(tailUp, tailDn, tailTip, f.Tail);
        else
            Raylib.DrawTriangle(tailTip, tailDn, tailUp, f.Tail);

        // Eye dot (white sclera + pupil) on the leading edge.
        float eyeX = origin.X + dirSign * f.Size * 0.45f;
        float eyeY = origin.Y - f.Size * 0.1f;
        Raylib.DrawCircle((int)eyeX, (int)eyeY, MathF.Max(2f, f.Size * 0.13f),
            new Color((byte)255, (byte)255, (byte)255, (byte)255));
        Raylib.DrawCircle((int)(eyeX + dirSign), (int)eyeY, MathF.Max(1f, f.Size * 0.07f),
            new Color((byte)20, (byte)20, (byte)30, (byte)255));
    }
}
