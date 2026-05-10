using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Scenes.Activities.Globe;

/// <summary>
/// Region-flavored critters that share the same Bayer-dithered Win98
/// silhouette language as <see cref="BearSwarm"/>. One instance is
/// populated per non-Ohio Earth hole — the kind of critter is chosen
/// from the active region (geese in North America, kangaroos in
/// Australia, etc.). Critters wander a small home area; if the ball
/// rolls into a critter's "snap" radius while moving slowly enough,
/// the critter snags it and the player eats a stroke + reset to tee.
/// </summary>
public class WildlifeSwarm
{
    public enum Species
    {
        None,
        Goose,        // North America — waddles near the cup, eats stationary balls
        Rabbit,       // Europe — hops between random points, snags slow balls
        Crane,        // Asia — stands near water, pecks at slow balls
        Parrot,       // South America — flits, plucks slow balls off the green
        Kangaroo,     // Australia — hops in big arcs, kick-eats slow balls
        Hippo,        // Africa — lounges near water, snaps at slow balls in range
    }

    private static readonly int[,] Bayer8 = new int[8, 8]
    {
        {  0, 32,  8, 40,  2, 34, 10, 42 },
        { 48, 16, 56, 24, 50, 18, 58, 26 },
        { 12, 44,  4, 36, 14, 46,  6, 38 },
        { 60, 28, 52, 20, 62, 30, 54, 22 },
        {  3, 35, 11, 43,  1, 33,  9, 41 },
        { 51, 19, 59, 27, 49, 17, 57, 25 },
        { 15, 47,  7, 39, 13, 45,  5, 37 },
        { 63, 31, 55, 23, 61, 29, 53, 21 },
    };

    public class Critter
    {
        public Species Kind;
        public Vector2 Pos;
        public Vector2 Vel;
        public Vector2 Home;
        public float PatrolR;
        public float Phase;
        public float Speed;
        public float Detect;       // ball-snap radius (against the body centre)
        public float Cooldown;     // post-snap pause
        public float ChirpTimer;
        public string ChirpText = "";
        public Texture2D Tex;
        public bool TexBuilt;
        // Hop / waddle bob animation timer — the sprite is offset
        // vertically by a small sin so creatures look alive even when
        // their world-Pos is parked.
        public float Bob;
        public bool FacingLeft;
    }

    public readonly List<Critter> Critters = new();

    /// <summary>
    /// Spawn the appropriate critters for a region's hole. Population
    /// counts and "preferred zone" (near cup vs near water) are picked
    /// per-species to fit the gameplay flavour.
    /// </summary>
    public void PopulateHole(Species kind, int holeIdx, int totalHoles,
                             Vector2 tee, Vector2 cup,
                             IReadOnlyList<(Vector2 Center, float Rx, float Ry, int Kind)> hazards,
                             int canvasW, int canvasH, Random rng)
    {
        Clear();
        if (kind == Species.None) return;

        // Per-species spawn count + "near water?" preference.
        int n;
        bool nearWater;
        switch (kind)
        {
            case Species.Goose:    n = 2 + rng.Next(2); nearWater = false; break;
            case Species.Rabbit:   n = 2 + rng.Next(2); nearWater = false; break;
            case Species.Crane:    n = 1 + rng.Next(2); nearWater = true;  break;
            case Species.Parrot:   n = 2 + rng.Next(3); nearWater = false; break;
            case Species.Kangaroo: n = 1 + rng.Next(2); nearWater = false; break;
            case Species.Hippo:    n = 1 + rng.Next(2); nearWater = true;  break;
            default: return;
        }

        for (int i = 0; i < n; i++)
        {
            Vector2 home = ChooseHome(tee, cup, hazards, nearWater, canvasW, canvasH, rng);
            Critters.Add(Make(kind, home, rng));
        }
    }

    public void Clear()
    {
        foreach (var c in Critters)
            if (c.TexBuilt) Raylib.UnloadTexture(c.Tex);
        Critters.Clear();
    }

    private static Vector2 ChooseHome(Vector2 tee, Vector2 cup,
        IReadOnlyList<(Vector2 Center, float Rx, float Ry, int Kind)> hazards,
        bool nearWater, int canvasW, int canvasH, Random rng)
    {
        // Try near-water first if requested — pick a water hazard, place
        // the critter just outside its rim. Falls back to free roaming
        // if no water exists on the hole.
        if (nearWater)
        {
            for (int tries = 0; tries < 8; tries++)
            {
                if (hazards.Count == 0) break;
                var (c, rx, ry, kind) = hazards[rng.Next(hazards.Count)];
                if (kind != 1) continue;        // 1 = water/goo
                double ang = rng.NextDouble() * Math.PI * 2;
                float r = (float)(0.85 + rng.NextDouble() * 0.25);
                var home = new Vector2(c.X + (float)Math.Cos(ang) * rx * r,
                                       c.Y + (float)Math.Sin(ang) * ry * r);
                if (Vector2.Distance(home, tee) > 50 &&
                    Vector2.Distance(home, cup) > 35)
                    return Clamp(home, canvasW, canvasH);
            }
        }
        for (int tries = 0; tries < 20; tries++)
        {
            var home = new Vector2(60 + (float)rng.NextDouble() * (canvasW - 120),
                                   40 + (float)rng.NextDouble() * (canvasH - 80));
            if (Vector2.Distance(home, tee) > 70 && Vector2.Distance(home, cup) > 45)
                return home;
        }
        return new Vector2(canvasW * 0.5f, canvasH * 0.5f);
    }

    private static Vector2 Clamp(Vector2 v, int canvasW, int canvasH) =>
        new(Math.Clamp(v.X, 12, canvasW - 12), Math.Clamp(v.Y, 12, canvasH - 12));

    private static Critter Make(Species kind, Vector2 home, Random rng)
    {
        // Per-species patrol envelope + how far they sense the ball.
        // Numbers tuned so each species feels distinct: hippos barely
        // move (water-bound), kangaroos cover huge arcs, parrots flit
        // small/fast.
        var c = new Critter
        {
            Kind  = kind,
            Pos   = home,
            Home  = home,
            Phase = (float)rng.NextDouble() * MathF.PI * 2f,
        };
        switch (kind)
        {
            case Species.Goose:
                c.PatrolR = 22f; c.Speed = 24f; c.Detect = 18f; break;
            case Species.Rabbit:
                c.PatrolR = 30f; c.Speed = 60f; c.Detect = 14f; break;
            case Species.Crane:
                c.PatrolR = 14f; c.Speed = 18f; c.Detect = 16f; break;
            case Species.Parrot:
                c.PatrolR = 40f; c.Speed = 70f; c.Detect = 12f; break;
            case Species.Kangaroo:
                c.PatrolR = 55f; c.Speed = 110f; c.Detect = 18f; break;
            case Species.Hippo:
                c.PatrolR = 8f;  c.Speed = 10f; c.Detect = 22f; break;
        }
        return c;
    }

    /// <summary>
    /// Per-frame simulation. Returns a non-zero penalty if a critter
    /// just snagged the ball (ball was slow + inside its snap radius).
    /// The caller resets the ball to tee + bumps stroke count.
    /// </summary>
    public AttackResult Update(float delta, Vector2 ballPos, Vector2 ballVel,
                               int canvasW, int canvasH)
    {
        AttackResult result = default;
        float ballSpeed = ballVel.Length();
        foreach (var c in Critters)
        {
            c.Cooldown   = MathF.Max(0, c.Cooldown - delta);
            c.ChirpTimer = MathF.Max(0, c.ChirpTimer - delta);
            c.Bob       += delta;

            // Wander in a per-species pattern. All variants use a
            // figure-8 around home; species differ in radius+speed.
            c.Phase += delta * (c.Kind == Species.Parrot ? 1.6f
                              : c.Kind == Species.Hippo  ? 0.25f : 0.7f);
            Vector2 target = c.Home + new Vector2(
                MathF.Cos(c.Phase) * c.PatrolR,
                MathF.Sin(c.Phase * 2f) * c.PatrolR * 0.5f);

            // Rabbits: hop *away* from a nearby ball instead of toward
            // the target — twitchy prey-animal feel.
            if (c.Kind == Species.Rabbit && Vector2.Distance(c.Pos, ballPos) < 60f)
            {
                var away = c.Pos - ballPos;
                if (away.LengthSquared() > 0.5f)
                    target = c.Pos + Vector2.Normalize(away) * 40f;
            }

            Vector2 toTarget = target - c.Pos;
            float len = toTarget.Length();
            Vector2 desired = len > 1f ? toTarget / len * c.Speed : Vector2.Zero;
            float steerRate = c.Kind == Species.Parrot   ? 6f
                            : c.Kind == Species.Hippo    ? 1f
                            : c.Kind == Species.Kangaroo ? 4f : 3f;
            c.Vel = Vector2.Lerp(c.Vel, desired, MathF.Min(1f, delta * steerRate));
            c.Pos += c.Vel * delta;
            if (c.Vel.X < -0.5f) c.FacingLeft = true;
            else if (c.Vel.X > 0.5f) c.FacingLeft = false;
            c.Pos.X = Math.Clamp(c.Pos.X, 12, canvasW - 12);
            c.Pos.Y = Math.Clamp(c.Pos.Y, 12, canvasH - 12);

            // Snap test: ball must be moving slowly enough that being
            // grabbed reads as fair play (a power shot whizzing past
            // doesn't get "eaten" mid-flight).
            if (c.Cooldown <= 0 && ballSpeed < 60f)
            {
                float distToBall = Vector2.Distance(c.Pos, ballPos);
                if (distToBall < c.Detect)
                {
                    c.Cooldown   = 2.0f;
                    c.ChirpText  = SnapText(c.Kind);
                    c.ChirpTimer = 1.4f;
                    result = new AttackResult
                    {
                        Hit = true,
                        StrokePenalty = 1,
                        Message = c.Kind switch
                        {
                            Species.Goose    => "A goose ate your ball!",
                            Species.Rabbit   => "A rabbit nicked your ball!",
                            Species.Crane    => "A crane pecked your ball!",
                            Species.Parrot   => "A parrot flew off with your ball!",
                            Species.Kangaroo => "A kangaroo kicked your ball away!",
                            Species.Hippo    => "A hippo chomped your ball!",
                            _ => "Critter grabbed your ball!",
                        },
                    };
                    // Only one snap per frame, and only the first
                    // critter wins. Bail out here so a flock doesn't
                    // pile multiple penalties on the same shot.
                    break;
                }
            }
        }
        return result;
    }

    private static string SnapText(Species kind) => kind switch
    {
        Species.Goose    => "HONK!",
        Species.Rabbit   => "boing!",
        Species.Crane    => "PECK!",
        Species.Parrot   => "squawk!",
        Species.Kangaroo => "BOING!",
        Species.Hippo    => "CHOMP!",
        _                => "!",
    };

    public void Draw(Vector2 canvasOrigin)
    {
        foreach (var c in Critters)
        {
            EnsureTexture(c);
            int w = c.Tex.Width, h = c.Tex.Height;
            int bobPx = (int)(MathF.Sin(c.Bob * BobFreq(c.Kind)) * BobAmp(c.Kind));
            int dx = (int)(canvasOrigin.X + c.Pos.X) - w / 2;
            int dy = (int)(canvasOrigin.Y + c.Pos.Y) - h * 3 / 4 + bobPx;

            // Soft contact shadow.
            Raylib.DrawEllipse(dx + w / 2, dy + h - 1,
                Math.Max(3, w / 2 - 2), 2, new Color((byte)0, (byte)0, (byte)0, (byte)110));

            if (c.FacingLeft)
            {
                // Mirror around the sprite's vertical centre by drawing
                // a flipped source rect.
                Raylib.DrawTexturePro(c.Tex,
                    new Rectangle(0, 0, -w, h),
                    new Rectangle(dx, dy, w, h),
                    Vector2.Zero, 0f, Color.White);
            }
            else
            {
                Raylib.DrawTexture(c.Tex, dx, dy, Color.White);
            }

            if (c.ChirpTimer > 0)
            {
                int gx = (int)(canvasOrigin.X + c.Pos.X) + 6;
                int gy = (int)(canvasOrigin.Y + c.Pos.Y) - h - 4
                       + (int)(MathF.Sin(c.ChirpTimer * 14f) * 1.5f);
                int sz = 11;
                int tw = MouseHouse.Scenes.Activities.Retro.RetroSkin.MeasureText(c.ChirpText, sz);
                Raylib.DrawRectangle(gx - 3, gy - 2, tw + 6, sz + 4,
                    new Color((byte)20, (byte)10, (byte)10, (byte)200));
                MouseHouse.Scenes.Activities.Retro.RetroSkin.DrawText(c.ChirpText, gx, gy,
                    new Color((byte)255, (byte)220, (byte)80, (byte)255), sz);
            }
        }
    }

    private static float BobFreq(Species kind) => kind switch
    {
        Species.Goose    => 5f,
        Species.Rabbit   => 9f,
        Species.Crane    => 2f,
        Species.Parrot   => 14f,
        Species.Kangaroo => 7f,
        Species.Hippo    => 1.5f,
        _                => 4f,
    };

    private static float BobAmp(Species kind) => kind switch
    {
        Species.Goose    => 1f,
        Species.Rabbit   => 3f,
        Species.Crane    => 1f,
        Species.Parrot   => 2f,
        Species.Kangaroo => 4f,
        Species.Hippo    => 1f,
        _                => 1f,
    };

    // ── Sprite baking ────────────────────────────────────────────────
    // All sprites are baked once into a small RGBA image with Bayer
    // dither + an upper-left key light, matching the BearSwarm look.
    // Each species has a hand-tuned palette and a small composition of
    // ellipses + accents drawn directly onto the pixel buffer.

    private static void EnsureTexture(Critter c)
    {
        if (c.TexBuilt) return;
        var img = c.Kind switch
        {
            Species.Goose    => BakeGoose(),
            Species.Rabbit   => BakeRabbit(),
            Species.Crane    => BakeCrane(),
            Species.Parrot   => BakeParrot(),
            Species.Kangaroo => BakeKangaroo(),
            Species.Hippo    => BakeHippo(),
            _                => Raylib.GenImageColor(8, 8, Color.Blank),
        };
        c.Tex = Raylib.LoadTextureFromImage(img);
        Raylib.SetTextureFilter(c.Tex, TextureFilter.Point);
        Raylib.UnloadImage(img);
        c.TexBuilt = true;
    }

    /// <summary>Common helper: Bayer-dither a body-mask through a
    /// brightness map (top lit, bottom shaded).</summary>
    private static void BlitBayer(ref Image img, int w, int h, Func<int, int, bool> mask,
                                  Color bright, Color dark, float topBias = 0.7f)
    {
        for (int py = 0; py < h; py++)
            for (int px = 0; px < w; px++)
            {
                if (!mask(px, py)) continue;
                float lambert = 1f - (py / (float)h) * topBias;
                int bayer = Bayer8[py & 7, px & 7];
                float threshold = bayer / 64f;
                Raylib.ImageDrawPixel(ref img, px, py,
                    lambert > threshold ? bright : dark);
            }
    }

    private static bool InEll(int x, int y, float cx, float cy, float rx, float ry)
    {
        float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry;
        return dx * dx + dy * dy <= 1f;
    }

    private static bool InCirc(int x, int y, float cx, float cy, float r)
    {
        float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
        return dx * dx + dy * dy <= r * r;
    }

    // ── Goose: white body, long black neck arched forward, orange beak ──
    private static Image BakeGoose()
    {
        const int w = 22, h = 20;
        var img = Raylib.GenImageColor(w, h, Color.Blank);
        var bodyBright = new Color((byte)244, (byte)244, (byte)244, (byte)255);
        var bodyDark   = new Color((byte)180, (byte)180, (byte)190, (byte)255);
        var neckBright = new Color((byte) 80, (byte) 80, (byte) 90, (byte)255);
        var neckDark   = new Color((byte) 16, (byte) 16, (byte) 24, (byte)255);
        var beak       = new Color((byte)232, (byte)160, (byte) 32, (byte)255);
        // Body (lower egg).
        BlitBayer(ref img, w, h,
            (x, y) => InEll(x, y, 12f, 13f, 8f, 5f),
            bodyBright, bodyDark);
        // Neck — slim ellipse curving forward.
        for (int t = 0; t < 14; t++)
        {
            float u = t / 13f;
            float nx = 12f - 8f * u;
            float ny = 13f - 9f * u + 1.5f * MathF.Sin(u * MathF.PI);
            BlitBayer(ref img, w, h,
                (x, y) => InCirc(x, y, nx, ny, 1.6f),
                neckBright, neckDark);
        }
        // Head.
        BlitBayer(ref img, w, h,
            (x, y) => InCirc(x, y, 4f, 3f, 2.5f),
            neckBright, neckDark);
        // Beak.
        Raylib.ImageDrawPixel(ref img, 1, 3, beak);
        Raylib.ImageDrawPixel(ref img, 0, 3, beak);
        Raylib.ImageDrawPixel(ref img, 1, 4, beak);
        // Eye dot.
        Raylib.ImageDrawPixel(ref img, 4, 2, new Color((byte)8, (byte)8, (byte)12, (byte)255));
        // Tail flick.
        Raylib.ImageDrawPixel(ref img, 20, 12, bodyDark);
        Raylib.ImageDrawPixel(ref img, 21, 11, bodyDark);
        // Orange feet.
        Raylib.ImageDrawPixel(ref img, 10, 19, beak);
        Raylib.ImageDrawPixel(ref img, 14, 19, beak);
        return img;
    }

    // ── Rabbit: gray-brown body, long ears, tiny tail ──
    private static Image BakeRabbit()
    {
        const int w = 18, h = 16;
        var img = Raylib.GenImageColor(w, h, Color.Blank);
        var fur   = new Color((byte)168, (byte)148, (byte)120, (byte)255);
        var furD  = new Color((byte) 96, (byte) 76, (byte) 56, (byte)255);
        // Body (big lower oval, hunched).
        BlitBayer(ref img, w, h,
            (x, y) => InEll(x, y, 9f, 11f, 6f, 4f),
            fur, furD);
        // Head.
        BlitBayer(ref img, w, h,
            (x, y) => InCirc(x, y, 5f, 7f, 3f),
            fur, furD);
        // Ears — two long verticals.
        for (int yy = 0; yy < 6; yy++)
        {
            Raylib.ImageDrawPixel(ref img, 4, yy, fur);
            Raylib.ImageDrawPixel(ref img, 6, yy, fur);
        }
        // Eye + nose.
        Raylib.ImageDrawPixel(ref img, 3, 7, new Color((byte)6, (byte)6, (byte)8, (byte)255));
        Raylib.ImageDrawPixel(ref img, 2, 8, new Color((byte)200, (byte)120, (byte)120, (byte)255));
        // Cotton-ball tail.
        var tail = new Color((byte)244, (byte)244, (byte)244, (byte)255);
        Raylib.ImageDrawPixel(ref img, 15, 11, tail);
        Raylib.ImageDrawPixel(ref img, 16, 11, tail);
        Raylib.ImageDrawPixel(ref img, 15, 12, tail);
        return img;
    }

    // ── Crane: tall white wader with long black legs and yellow beak ──
    private static Image BakeCrane()
    {
        const int w = 16, h = 26;
        var img = Raylib.GenImageColor(w, h, Color.Blank);
        var feathers  = new Color((byte)244, (byte)244, (byte)244, (byte)255);
        var fShade    = new Color((byte)164, (byte)164, (byte)180, (byte)255);
        var legBright = new Color((byte) 60, (byte) 56, (byte) 56, (byte)255);
        var legDark   = new Color((byte) 16, (byte) 16, (byte) 16, (byte)255);
        var beak      = new Color((byte)232, (byte)188, (byte) 60, (byte)255);
        // Body — small oval high on the canvas (long legs below).
        BlitBayer(ref img, w, h,
            (x, y) => InEll(x, y, 9f, 12f, 4.5f, 3.5f),
            feathers, fShade);
        // Long S-curve neck.
        for (int t = 0; t < 9; t++)
        {
            float u = t / 8f;
            float nx = 9f - 3f * MathF.Sin(u * MathF.PI);
            float ny = 11f - 7f * u;
            BlitBayer(ref img, w, h,
                (x, y) => InCirc(x, y, nx, ny, 1.2f),
                feathers, fShade);
        }
        // Head.
        BlitBayer(ref img, w, h,
            (x, y) => InCirc(x, y, 6f, 4f, 1.8f),
            feathers, fShade);
        // Beak — pointing left.
        Raylib.ImageDrawPixel(ref img, 4, 4, beak);
        Raylib.ImageDrawPixel(ref img, 3, 4, beak);
        // Eye.
        Raylib.ImageDrawPixel(ref img, 7, 3, new Color((byte)10, (byte)6, (byte)4, (byte)255));
        // Two long legs.
        for (int yy = 16; yy < h - 1; yy++)
        {
            int bayer = Bayer8[yy & 7, 0];
            var lc = bayer < 32 ? legBright : legDark;
            Raylib.ImageDrawPixel(ref img, 8, yy, lc);
            Raylib.ImageDrawPixel(ref img, 11, yy, lc);
        }
        return img;
    }

    // ── Parrot: small green body, red head, hooked yellow beak ──
    private static Image BakeParrot()
    {
        const int w = 14, h = 14;
        var img = Raylib.GenImageColor(w, h, Color.Blank);
        var green   = new Color((byte) 64, (byte)180, (byte) 88, (byte)255);
        var greenD  = new Color((byte) 24, (byte)100, (byte) 48, (byte)255);
        var red     = new Color((byte)220, (byte) 64, (byte) 56, (byte)255);
        var redD    = new Color((byte)136, (byte) 24, (byte) 24, (byte)255);
        var beak    = new Color((byte)244, (byte)204, (byte) 60, (byte)255);
        BlitBayer(ref img, w, h,
            (x, y) => InEll(x, y, 8f, 9f, 4.5f, 3.5f),
            green, greenD);
        BlitBayer(ref img, w, h,
            (x, y) => InCirc(x, y, 4f, 5f, 2.5f),
            red, redD);
        // Beak — left-pointing hook.
        Raylib.ImageDrawPixel(ref img, 2, 5, beak);
        Raylib.ImageDrawPixel(ref img, 1, 5, beak);
        Raylib.ImageDrawPixel(ref img, 2, 6, beak);
        // Eye.
        Raylib.ImageDrawPixel(ref img, 5, 4, new Color((byte)4, (byte)4, (byte)4, (byte)255));
        // Tail feathers (2 px) trailing right.
        Raylib.ImageDrawPixel(ref img, 13, 9, green);
        Raylib.ImageDrawPixel(ref img, 13, 10, greenD);
        // Tiny feet.
        Raylib.ImageDrawPixel(ref img,  6, 13, beak);
        Raylib.ImageDrawPixel(ref img,  9, 13, beak);
        return img;
    }

    // ── Kangaroo: tan body, big haunches, long tail ──
    private static Image BakeKangaroo()
    {
        const int w = 22, h = 26;
        var img = Raylib.GenImageColor(w, h, Color.Blank);
        var fur  = new Color((byte)196, (byte)128, (byte) 80, (byte)255);
        var furD = new Color((byte)112, (byte) 60, (byte) 32, (byte)255);
        // Big haunch (lower-right oval).
        BlitBayer(ref img, w, h,
            (x, y) => InEll(x, y, 14f, 18f, 6.5f, 6f),
            fur, furD);
        // Belly / chest — upper torso oval.
        BlitBayer(ref img, w, h,
            (x, y) => InEll(x, y, 11f, 12f, 4.5f, 5.5f),
            fur, furD);
        // Head.
        BlitBayer(ref img, w, h,
            (x, y) => InEll(x, y, 8f, 6f, 3f, 3f),
            fur, furD);
        // Snout.
        BlitBayer(ref img, w, h,
            (x, y) => InEll(x, y, 5f, 7f, 2f, 1.4f),
            fur, furD);
        // Big upright ears.
        for (int yy = 0; yy < 4; yy++)
        {
            Raylib.ImageDrawPixel(ref img, 7, yy, fur);
            Raylib.ImageDrawPixel(ref img, 9, yy, fur);
        }
        // Eye.
        Raylib.ImageDrawPixel(ref img, 6, 6, new Color((byte)6, (byte)6, (byte)8, (byte)255));
        // Tail — thick curve trailing back-right.
        for (int t = 0; t < 9; t++)
        {
            float u = t / 8f;
            float tx = 14f + 6f * u;
            float ty = 23f - 5f * u + 2f * MathF.Sin(u * MathF.PI);
            BlitBayer(ref img, w, h,
                (x, y) => InCirc(x, y, tx, ty, 1.6f),
                fur, furD);
        }
        // Feet.
        Raylib.ImageDrawPixel(ref img, 11, 25, furD);
        Raylib.ImageDrawPixel(ref img, 12, 25, furD);
        Raylib.ImageDrawPixel(ref img, 13, 25, furD);
        return img;
    }

    // ── Hippo: stout gray barrel with little ears + nostrils ──
    private static Image BakeHippo()
    {
        const int w = 32, h = 18;
        var img = Raylib.GenImageColor(w, h, Color.Blank);
        var skin  = new Color((byte)160, (byte)112, (byte)112, (byte)255);
        var skinD = new Color((byte) 88, (byte) 56, (byte) 60, (byte)255);
        // Body — big horizontal oval.
        BlitBayer(ref img, w, h,
            (x, y) => InEll(x, y, 18f, 11f, 11f, 5f),
            skin, skinD);
        // Head — bulge on left.
        BlitBayer(ref img, w, h,
            (x, y) => InEll(x, y, 6f, 9f, 5f, 4.5f),
            skin, skinD);
        // Snout — flat squarish nose-bump.
        BlitBayer(ref img, w, h,
            (x, y) => InEll(x, y, 2f, 11f, 2.5f, 2f),
            skin, skinD);
        // Ears — tiny circles up top.
        BlitBayer(ref img, w, h,
            (x, y) => InCirc(x, y, 7f, 4f, 1.2f),
            skin, skinD);
        BlitBayer(ref img, w, h,
            (x, y) => InCirc(x, y, 4f, 4f, 1.2f),
            skin, skinD);
        // Nostrils + eye — pixel accents.
        Raylib.ImageDrawPixel(ref img, 1, 10, new Color((byte)8, (byte)8, (byte)12, (byte)255));
        Raylib.ImageDrawPixel(ref img, 1, 12, new Color((byte)8, (byte)8, (byte)12, (byte)255));
        Raylib.ImageDrawPixel(ref img, 6, 7, new Color((byte)8, (byte)8, (byte)12, (byte)255));
        // Subtle waterline highlight along the bottom — brightens the
        // contact band so the hippo reads as half-submerged.
        var wl = new Color((byte)196, (byte)160, (byte)164, (byte)255);
        for (int xx = 8; xx < 28; xx++)
            Raylib.ImageDrawPixel(ref img, xx, 14, wl);
        return img;
    }
}
