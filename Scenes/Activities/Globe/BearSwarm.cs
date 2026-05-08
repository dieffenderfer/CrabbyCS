using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Scenes.Activities.Globe;

/// <summary>
/// Dither-art bears that roam Ohio courses harassing the player. Same
/// 8x8 Bayer ordered-dither look as the globe so the visual language is
/// consistent. Bears patrol around a home point; if the ball gets close
/// they switch to chase mode and shoulder-tackle it on contact (one
/// stroke penalty + the ball gets shoved back along the line of attack).
/// One bear per Ohio hole is a "Mama Bear" — bigger, faster, longer
/// detection range, and her growl text reads BIGGER too.
/// </summary>
public class BearSwarm
{
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

    public class Bear
    {
        public Vector2 Pos;
        public Vector2 Vel;
        public Vector2 Home;
        public float PatrolR;
        public float Phase;
        public bool IsBoss;
        public float Speed;
        public float Detect;
        public float Cooldown;       // post-attack pause so a single contact doesn't burn 5 strokes
        public float GrowlTimer;
        public string GrowlText = "";
        // Cached pre-baked dither sprite. Built once on first use; dimensions
        // depend on IsBoss so the boss reads bigger on screen.
        public Texture2D Tex;
        public bool TexBuilt;
    }

    public readonly List<Bear> Bears = new();

    /// <summary>
    /// Set up bears for an Ohio hole: 2-4 patrolling regulars plus a Mama
    /// boss on the last hole. Avoids spawning on top of the tee or the
    /// cup — the player needs at least a chance to set up their first shot.
    /// </summary>
    public void PopulateOhioHole(int holeIdx, int totalHoles, Vector2 tee, Vector2 cup,
                                 int canvasW, int canvasH, Random rng)
    {
        Clear();
        int regular = 2 + rng.Next(3);     // 2-4 patrolling regulars
        bool bossHole = holeIdx == totalHoles - 1;

        for (int i = 0; i < regular; i++)
            Bears.Add(SpawnAwayFrom(tee, cup, canvasW, canvasH, rng, isBoss: false));
        if (bossHole)
            Bears.Add(SpawnAwayFrom(tee, cup, canvasW, canvasH, rng, isBoss: true));
    }

    public void Clear()
    {
        foreach (var b in Bears)
            if (b.TexBuilt) Raylib.UnloadTexture(b.Tex);
        Bears.Clear();
    }

    private static Bear SpawnAwayFrom(Vector2 tee, Vector2 cup,
                                      int canvasW, int canvasH, Random rng, bool isBoss)
    {
        Vector2 home;
        for (int tries = 0; tries < 20; tries++)
        {
            home = new Vector2(60 + (float)rng.NextDouble() * (canvasW - 120),
                               40 + (float)rng.NextDouble() * (canvasH - 80));
            if (Vector2.Distance(home, tee) > 80 && Vector2.Distance(home, cup) > 60)
                return Make(home, rng, isBoss);
        }
        // Fallback: anywhere away from tee horizontally.
        home = new Vector2(canvasW * 0.5f, canvasH * 0.5f);
        return Make(home, rng, isBoss);
    }

    private static Bear Make(Vector2 home, Random rng, bool isBoss)
    {
        return new Bear
        {
            Pos = home,
            Home = home,
            PatrolR = isBoss ? 60f : 35f + (float)rng.NextDouble() * 25f,
            Phase = (float)rng.NextDouble() * MathF.PI * 2f,
            IsBoss = isBoss,
            Speed = isBoss ? 70f : 38f + (float)rng.NextDouble() * 12f,
            Detect = isBoss ? 130f : 80f,
        };
    }

    /// <summary>
    /// Per-frame update. Returns a non-zero "attack impulse" if any bear
    /// just made contact with the ball this frame; the caller applies the
    /// stroke penalty + the displacement to ball velocity.
    /// </summary>
    public AttackResult Update(float delta, Vector2 ballPos, ref Vector2 ballVel,
                               int canvasW, int canvasH)
    {
        AttackResult result = default;
        foreach (var b in Bears)
        {
            b.GrowlTimer = MathF.Max(0, b.GrowlTimer - delta);
            b.Cooldown = MathF.Max(0, b.Cooldown - delta);

            float distToBall = Vector2.Distance(b.Pos, ballPos);
            bool chase = b.Cooldown <= 0 && distToBall < b.Detect;
            Vector2 desired;
            if (chase)
            {
                // First time spotting — emit a growl. Small chance to also
                // re-growl if the player ducks back into range later.
                if (b.GrowlTimer <= 0 && distToBall < b.Detect * 0.85f)
                {
                    b.GrowlText = b.IsBoss ? "ROOOAR!" : "GRR!";
                    b.GrowlTimer = b.IsBoss ? 1.4f : 0.9f;
                }
                Vector2 toBall = ballPos - b.Pos;
                float len = toBall.Length();
                desired = len > 0.5f ? toBall / len * b.Speed : Vector2.Zero;
            }
            else
            {
                // Lazy figure-8 patrol around home so the silhouettes look
                // alive even when the player is far away.
                b.Phase += delta * 0.7f;
                Vector2 target = b.Home + new Vector2(
                    MathF.Cos(b.Phase) * b.PatrolR,
                    MathF.Sin(b.Phase * 2f) * b.PatrolR * 0.5f);
                Vector2 toTarget = target - b.Pos;
                float len = toTarget.Length();
                desired = len > 1f ? toTarget / len * (b.Speed * 0.4f) : Vector2.Zero;
            }
            // Steer rather than snap so the bears feel weighty.
            b.Vel = Vector2.Lerp(b.Vel, desired, MathF.Min(1f, delta * 3f));
            b.Pos += b.Vel * delta;
            // Keep on canvas.
            b.Pos.X = Math.Clamp(b.Pos.X, 12, canvasW - 12);
            b.Pos.Y = Math.Clamp(b.Pos.Y, 12, canvasH - 12);

            // Attack: contact if the bear's centre is within its body-radius
            // of the ball. Cooldown prevents repeated charges on the same
            // shot.
            float bodyR = b.IsBoss ? 14f : 10f;
            if (b.Cooldown <= 0 && distToBall < bodyR + 5f)
            {
                Vector2 from = b.Pos;
                Vector2 dir = ballPos - from;
                float l = dir.Length();
                Vector2 push = l > 0.01f ? dir / l : Vector2.UnitX;
                // Knock the ball away from the bear with a meaty impulse;
                // also bleed any forward momentum so the player doesn't
                // get a free roll-out.
                ballVel = push * (b.IsBoss ? 240f : 170f);
                result = new AttackResult
                {
                    Hit = true,
                    BossHit = b.IsBoss,
                    StrokePenalty = 1,
                    Message = b.IsBoss ? "Mama Bear charged you!" : "Bear shoulder-tackle!",
                };
                b.Cooldown = b.IsBoss ? 2.5f : 1.8f;
                b.GrowlText = b.IsBoss ? "RAAAAH!" : "RAARGH!";
                b.GrowlTimer = 1.5f;
            }
        }
        return result;
    }

    public void Draw(Vector2 canvasOrigin)
    {
        foreach (var b in Bears)
        {
            EnsureTexture(b);
            int w = b.Tex.Width, h = b.Tex.Height;
            int dx = (int)(canvasOrigin.X + b.Pos.X) - w / 2;
            int dy = (int)(canvasOrigin.Y + b.Pos.Y) - h * 3 / 4;   // anchor at feet, not centre
            // Soft shadow.
            Raylib.DrawEllipse(dx + w / 2, dy + h - 2, w / 2 - 2, 3,
                new Color((byte)0, (byte)0, (byte)0, (byte)110));
            Raylib.DrawTexture(b.Tex, dx, dy, Color.White);

            // Growl bubble.
            if (b.GrowlTimer > 0)
            {
                float t = b.GrowlTimer;
                int gx = (int)(canvasOrigin.X + b.Pos.X) + 8;
                int gy = (int)(canvasOrigin.Y + b.Pos.Y) - h - 4
                       + (int)(MathF.Sin(t * 14f) * 1.5f);
                int sz = b.IsBoss ? 14 : 11;
                int tw = MouseHouse.Scenes.Activities.Retro.RetroSkin.MeasureText(b.GrowlText, sz);
                var bg = new Color((byte)20, (byte)10, (byte)10, (byte)200);
                Raylib.DrawRectangle(gx - 3, gy - 2, tw + 6, sz + 4, bg);
                MouseHouse.Scenes.Activities.Retro.RetroSkin.DrawText(b.GrowlText, gx, gy,
                    new Color((byte)255, (byte)220, (byte)80, (byte)255), sz);
            }
        }
    }

    private static void EnsureTexture(Bear b)
    {
        if (b.TexBuilt) return;
        // Bear silhouette baked into a small RGBA image with Bayer dither.
        // Boss is taller/wider so the player feels the threat at a glance.
        int w = b.IsBoss ? 36 : 26;
        int h = b.IsBoss ? 30 : 22;
        var img = Raylib.GenImageColor(w, h, Color.Blank);
        var pal = b.IsBoss
            ? (Bright: new Color((byte)160, (byte)80, (byte)40, (byte)255),
               Dark:   new Color((byte)40, (byte)16, (byte)8, (byte)255))
            : (Bright: new Color((byte)120, (byte)80, (byte)50, (byte)255),
               Dark:   new Color((byte)32, (byte)18, (byte)10, (byte)255));

        // Anatomy as a sum of ellipses in local pixel space:
        //   body: lower-centre big ellipse (hunched stance)
        //   head: upper-centre slightly-smaller circle, leaning forward
        //   ears: two small circles on top of the head, splayed
        //   snout: short bump in front of the head
        float bodyCx = w * 0.5f, bodyCy = h * 0.62f;
        float bodyRx = w * 0.42f, bodyRy = h * 0.30f;
        float headCx = w * 0.36f, headCy = h * 0.30f;
        float headR  = w * 0.18f;
        float earOff = headR * 0.85f;
        float earR   = headR * 0.42f;
        float snoutCx = w * 0.22f, snoutCy = headCy + headR * 0.20f;
        float snoutR  = headR * 0.45f;

        bool InsideEll(float x, float y, float cx, float cy, float rx, float ry)
        {
            float dx = (x - cx) / rx, dy = (y - cy) / ry;
            return dx * dx + dy * dy <= 1f;
        }
        bool InsideCirc(float x, float y, float cx, float cy, float r)
        {
            float dx = x - cx, dy = y - cy;
            return dx * dx + dy * dy <= r * r;
        }

        for (int py = 0; py < h; py++)
            for (int px = 0; px < w; px++)
            {
                float fx = px + 0.5f, fy = py + 0.5f;
                bool inside = InsideEll(fx, fy, bodyCx, bodyCy, bodyRx, bodyRy)
                            || InsideCirc(fx, fy, headCx, headCy, headR)
                            || InsideCirc(fx, fy, headCx - earOff * 0.5f, headCy - earOff * 0.7f, earR)
                            || InsideCirc(fx, fy, headCx + earOff * 0.5f, headCy - earOff * 0.7f, earR)
                            || InsideCirc(fx, fy, snoutCx, snoutCy, snoutR);
                if (!inside) continue;

                // Vertical brightness gradient — top lit, bottom shadowed —
                // matches the upper-left key light the globe uses, so the
                // bear feels like it lives in the same scene.
                float lambert = 1f - (py / (float)h) * 0.7f;
                int bayer = Bayer8[py & 7, px & 7];
                float threshold = bayer / 64f;
                var col = lambert > threshold ? pal.Bright : pal.Dark;
                Raylib.ImageDrawPixel(ref img, px, py, col);
            }

        // Tiny eye + nose hint so the silhouette reads as a face on close-up.
        Raylib.ImageDrawPixel(ref img, (int)(headCx + 1), (int)(headCy - 1),
            new Color((byte)255, (byte)240, (byte)180, (byte)255));
        Raylib.ImageDrawPixel(ref img, (int)snoutCx, (int)snoutCy,
            new Color((byte)10, (byte)6, (byte)4, (byte)255));

        b.Tex = Raylib.LoadTextureFromImage(img);
        Raylib.SetTextureFilter(b.Tex, TextureFilter.Point);
        Raylib.UnloadImage(img);
        b.TexBuilt = true;
    }
}

public struct AttackResult
{
    public bool Hit;
    public bool BossHit;
    public int StrokePenalty;
    public string? Message;
}
