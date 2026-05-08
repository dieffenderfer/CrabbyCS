using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// About / Credits — a scrollable list of every third-party asset and
/// library bundled with the app, grouped by the module it lives in. The
/// actual data sits in the static <see cref="Modules"/> table near the top
/// of the file so it's easy to edit a single line when a source needs to
/// be filled in.
///
/// "Source: TBD" entries are intentional placeholders — the wav files
/// themselves are bundled and licensed however they came in; this UI is
/// just where we surface attribution to the user. Edit the strings in
/// <see cref="Modules"/> to replace TBD with the real source.
/// </summary>
public class CreditsActivity : IActivity
{
    public Vector2 PanelSize => new(640, 520);
    public bool IsFinished { get; private set; }

    private float _scroll;
    private const int TitleBarH = 28;
    private const int Padding = 16;
    private const int SectionGap = 14;
    private const int LineH = 16;
    private const int SectionHeaderH = 22;

    private record Entry(string Name, string License, string Source);
    private record Module(string Title, Entry[] Items);

    // Tabular credits data. To fill in a missing source, edit the third
    // column of the row in question — that's the only place the string is
    // referenced. Keep entries grouped by the module folder under assets/.
    private static readonly Module[] Modules =
    {
        new("Code / Libraries", new[]
        {
            new Entry("Raylib-cs 6.1.1",   "zlib", "github.com/ChrisDill/Raylib-cs"),
            new Entry("ENet-CSharp 2.4.8", "MIT",  "github.com/SoftwareGuy/ENet-CSharp"),
            new Entry(".NET 10 runtime",   "MIT",  "Microsoft"),
        }),

        new("Core — Fonts", new[]
        {
            new Entry("W95F.otf (W95FA)",          "SIL OFL 1.1", "Style-7 / W95FA project (see W95FA-OFL.txt)"),
            new Entry("Press Start 2P",             "SIL OFL 1.1", "Cody Boisclair (codeman38)"),
            new Entry("Silkscreen / Silkscreen Bold","SIL OFL 1.1", "Jason Kottke"),
            new Entry("VT323",                      "SIL OFL 1.1", "Peter Hull"),
            new Entry("DotGothic16",                "SIL OFL 1.1", "Fontworks Inc."),
            new Entry("Pixelify Sans",              "SIL OFL 1.1", "Stefan Pentcheff"),
            new Entry("Jersey 10 / Jersey 15",      "SIL OFL 1.1", "Sarah Cadigan-Fried"),
            new Entry("Share Tech Mono",            "SIL OFL 1.1", "Carrois Apostrophe"),
            new Entry("Bungee Shade",               "SIL OFL 1.1", "David Jonathan Ross"),
            new Entry("Tiny5",                      "SIL OFL 1.1", "Velvetyne Type Foundry"),
            new Entry("Geo",                        "SIL OFL 1.1", "Ben Weiner / Reading Type"),
            new Entry("Jacquard 12",                "SIL OFL 1.1", "Sasha Trubetskoy"),
            new Entry("Matemasie",                  "SIL OFL 1.1", "Vibrant Type"),
            new Entry("Orbitron",                   "SIL OFL 1.1", "Matt McInerney"),
            new Entry("Inconsolata",                "SIL OFL 1.1", "Raph Levien"),
            new Entry("Rubik Mono One",             "SIL OFL 1.1", "Philatype / Cyreal"),
            new Entry("Special Elite",              "Apache 2.0",  "Astigmatic"),
            new Entry("Coustard",                   "SIL OFL 1.1", "The League of Moveable Type"),
            new Entry("Righteous",                  "SIL OFL 1.1", "Astigmatic"),
            new Entry("Lora",                       "SIL OFL 1.1", "Cyreal"),
            new Entry("Concert One",                "SIL OFL 1.1", "Johan Kallas / Mihkel Virkus"),
            new Entry("Fredoka",                    "SIL OFL 1.1", "Milena Brandao"),
            new Entry("Permanent Marker",           "Apache 2.0",  "Font Diner"),
            new Entry("Audiowide",                  "SIL OFL 1.1", "Astigmatic"),
            new Entry("DejaVu Sans (fallback)",     "DejaVu",      "DejaVu Fonts (see DejaVuSans-LICENSE.txt)"),
        }),

        new("Core — Sounds", new[]
        {
            new Entry("click.wav",        "TBD", "TBD — fill in"),
            new Entry("select.wav",       "TBD", "TBD — fill in"),
            new Entry("win_fanfare.wav",  "TBD", "TBD — fill in"),
            new Entry("splash.wav",       "TBD", "TBD — fill in"),
            new Entry("rain_light.wav",   "TBD", "TBD — fill in"),
            new Entry("rain_loop.wav",    "TBD", "TBD — fill in"),
            new Entry("wind_strong.wav",  "TBD", "TBD — fill in"),
        }),

        new("Cards — Sounds", new[]
        {
            new Entry("card_flip.wav",  "TBD", "TBD — fill in"),
            new Entry("card_place.wav", "TBD", "TBD — fill in"),
        }),

        new("Cooking — Sounds", new[]
        {
            new Entry("chop.wav",             "TBD", "TBD — fill in"),
            new Entry("recipe_complete.wav",  "TBD", "TBD — fill in"),
            new Entry("sizzle.wav",           "TBD", "TBD — fill in"),
            new Entry("splat.wav",            "TBD", "TBD — fill in"),
        }),

        new("Dance — Sounds", new[]
        {
            new Entry("dance_music_loop.wav", "TBD", "TBD — fill in"),
            new Entry("hit_good.wav",         "TBD", "TBD — fill in"),
            new Entry("hit_miss.wav",         "TBD", "TBD — fill in"),
            new Entry("score_reveal.wav",     "TBD", "TBD — fill in"),
        }),

        new("Fishing — Sounds", new[]
        {
            new Entry("cast.wav",          "TBD", "TBD — fill in"),
            new Entry("reel.wav",          "TBD", "TBD — fill in"),
            new Entry("catch_jingle.wav",  "TBD", "TBD — fill in"),
            new Entry("got_away.wav",      "TBD", "TBD — fill in"),
        }),

        new("Gardening — Sounds", new[]
        {
            new Entry("plant.wav",   "TBD", "TBD — fill in"),
            new Entry("water.wav",   "TBD", "TBD — fill in"),
            new Entry("grow.wav",    "TBD", "TBD — fill in"),
            new Entry("harvest.wav", "TBD", "TBD — fill in"),
        }),

        new("Golf — Sprites", new[]
        {
            new Entry("tree.png (shaggy fir)", "Original", "This project (procedurally generated)"),
        }),

        new("Golf — Sounds", new[]
        {
            new Entry("golfswing.wav",         "TBD", "TBD — fill in"),
            new Entry("sinking_golf_ball.wav", "TBD", "TBD — fill in"),
        }),

        new("Kite Flying — Sounds", new[]
        {
            new Entry("kite_whoosh.wav", "TBD", "TBD — fill in"),
            new Entry("wind_gentle.wav", "TBD", "TBD — fill in"),
        }),

        new("Paint — Sprites", new[]
        {
            new Entry("tools.png (16×16 toolbar)", "MIT", "jspaint by Isaiah Odhner (github.com/1j01/jspaint)"),
        }),

        new("Stargazing — Sounds", new[]
        {
            new Entry("night_ambient.wav",          "TBD", "TBD — fill in"),
            new Entry("star_connect.wav",           "TBD", "TBD — fill in"),
            new Entry("constellation_complete.wav", "TBD", "TBD — fill in"),
            new Entry("shooting_star.wav",          "TBD", "TBD — fill in"),
        }),

        new("Zones — Sounds", new[]
        {
            new Entry("zone_beach_ambient.wav",     "TBD", "TBD — fill in"),
            new Entry("zone_apartment_ambient.wav", "TBD", "TBD — fill in"),
            new Entry("zone_bedroom_ambient.wav",   "TBD", "TBD — fill in"),
            new Entry("zone_camping_ambient.wav",   "TBD", "TBD — fill in"),
            new Entry("ocean_waves.wav",            "TBD", "TBD — fill in"),
            new Entry("fireplace.wav",              "TBD", "TBD — fill in"),
            new Entry("footstep.wav",               "TBD", "TBD — fill in"),
            new Entry("door.wav",                   "TBD", "TBD — fill in"),
        }),
    };

    public CreditsActivity(AssetCache assets) { _ = assets; }

    public void Load() { }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
        bool leftPressed, bool leftReleased, bool rightPressed)
    {
        _scroll -= Raylib.GetMouseWheelMove() * 32f;
        if (Raylib.IsKeyPressed(KeyboardKey.Down)) _scroll += 32f;
        if (Raylib.IsKeyPressed(KeyboardKey.Up)) _scroll -= 32f;
        if (Raylib.IsKeyPressed(KeyboardKey.PageDown)) _scroll += 200f;
        if (Raylib.IsKeyPressed(KeyboardKey.PageUp)) _scroll -= 200f;
        _scroll = Math.Clamp(_scroll, 0, MaxScroll());

        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) IsFinished = true;

        if (leftPressed)
        {
            var closeRect = new Rectangle(
                panelOffset.X + PanelSize.X - 36, panelOffset.Y + 4, 28, TitleBarH - 8);
            if (Raylib.CheckCollisionPointRec(mousePos, closeRect))
                IsFinished = true;
        }
    }

    private float ContentHeight()
    {
        float h = Padding;
        for (int m = 0; m < Modules.Length; m++)
        {
            h += SectionHeaderH;
            h += Modules[m].Items.Length * LineH;
            h += SectionGap;
        }
        return h;
    }

    private float MaxScroll()
    {
        float visible = PanelSize.Y - TitleBarH - Padding;
        return Math.Max(0, ContentHeight() - visible);
    }

    public void Draw(Vector2 offset)
    {
        int px = (int)offset.X, py = (int)offset.Y;
        int pw = (int)PanelSize.X, ph = (int)PanelSize.Y;

        Raylib.DrawRectangle(px, py, pw, ph, new Color((byte)25, (byte)25, (byte)30, (byte)255));
        Raylib.DrawRectangleLines(px, py, pw, ph, new Color((byte)60, (byte)60, (byte)65, (byte)255));

        // Title bar
        Raylib.DrawRectangle(px, py, pw, TitleBarH, new Color((byte)45, (byte)45, (byte)50, (byte)255));
        Raylib.DrawText("About — Credits & Attributions", px + 10, py + 7, 14,
            new Color((byte)200, (byte)200, (byte)200, (byte)255));
        Raylib.DrawText("[X]", px + pw - 32, py + 7, 14,
            new Color((byte)200, (byte)100, (byte)100, (byte)255));

        // Manual y-clipping (avoid BeginScissorMode — has macOS Retina pixel
        // bugs in this project; see project memory).
        int contentTop = py + TitleBarH + 4;
        int contentBot = py + ph - 4;
        float y = contentTop + Padding - _scroll;

        var headerColor = new Color((byte)180, (byte)220, (byte)255, (byte)255);
        var nameColor   = new Color((byte)225, (byte)225, (byte)225, (byte)255);
        var licenseColor= new Color((byte)160, (byte)200, (byte)160, (byte)255);
        var sourceColor = new Color((byte)160, (byte)160, (byte)170, (byte)255);
        var tbdColor    = new Color((byte)220, (byte)170, (byte)100, (byte)255);
        var dividerColor= new Color((byte)55, (byte)55, (byte)60, (byte)255);

        foreach (var module in Modules)
        {
            // Section header
            if (y + SectionHeaderH > contentTop && y < contentBot)
            {
                Raylib.DrawText(module.Title, px + Padding, (int)y + 4, 14, headerColor);
                Raylib.DrawLineEx(
                    new Vector2(px + Padding, y + SectionHeaderH - 4),
                    new Vector2(px + pw - Padding, y + SectionHeaderH - 4),
                    1, dividerColor);
            }
            y += SectionHeaderH;

            // Entries
            foreach (var entry in module.Items)
            {
                if (y + LineH > contentTop && y < contentBot)
                {
                    bool isTbd = entry.License == "TBD";
                    // Three columns: name (left), license (center-ish), source (right of license).
                    int nameX = px + Padding + 4;
                    int licenseX = px + 240;
                    int sourceX = px + 320;

                    Raylib.DrawText(entry.Name, nameX, (int)y, 12, nameColor);
                    Raylib.DrawText(entry.License, licenseX, (int)y, 12,
                        isTbd ? tbdColor : licenseColor);
                    Raylib.DrawText(entry.Source, sourceX, (int)y, 12,
                        isTbd ? tbdColor : sourceColor);
                }
                y += LineH;
            }

            y += SectionGap;
        }

        // Scroll hint at the bottom-right of the title bar so users know
        // they can wheel-scroll through the list.
        float frac = MaxScroll() <= 0 ? 0f : _scroll / MaxScroll();
        int trackX = px + pw - 14;
        int trackTop = py + TitleBarH + 4;
        int trackH = ph - TitleBarH - 8;
        Raylib.DrawRectangle(trackX, trackTop, 4, trackH, new Color((byte)40, (byte)40, (byte)45, (byte)255));
        int thumbH = Math.Max(20, (int)(trackH * (PanelSize.Y / Math.Max(1f, ContentHeight()))));
        int thumbY = trackTop + (int)((trackH - thumbH) * frac);
        Raylib.DrawRectangle(trackX, thumbY, 4, thumbH, new Color((byte)90, (byte)90, (byte)100, (byte)255));
    }

    public void Close() { }
}
