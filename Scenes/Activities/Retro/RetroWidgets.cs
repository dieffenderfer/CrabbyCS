using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Scenes.Activities.Retro;

/// <summary>
/// Retro 90s-desktop widget kit. Each widget is split into a HitTest (called
/// from Update — input only, returns interaction) and a Visual (called from
/// Draw — render only). Activities own the per-widget "armed" booleans so
/// state survives across frames without a retained widget tree.
/// </summary>
public static class RetroWidgets
{
    public const int TitleBarHeight = 18;
    public const int MenuBarHeight = 20;
    public const int StatusBarHeight = 24;
    // Mutable so the in-app font debug panel can tweak it at runtime.
    public static int StatusFontSize = 14;
    public const int BorderThickness = 3;

    public static void DrawWindowFrame(Rectangle panel) => RetroSkin.DrawRaised(panel);

    // ── Title bar ────────────────────────────────────────────────────────
    public static void DrawTitleBarVisual(Rectangle bar, string title, bool active, int titleYOffset = 0, bool includeMinimize = true)
    {
        if (active)
        {
            Raylib.DrawRectangleGradientH((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height,
                RetroSkin.TitleActive, RetroSkin.TitleGradEnd);
        }
        else
        {
            Raylib.DrawRectangleRec(bar, RetroSkin.TitleInactive);
        }
        RetroSkin.DrawText(title, (int)bar.X + 4, (int)bar.Y + 2 + titleYOffset,
            RetroSkin.TitleText, RetroSkin.TitleFontSize);

        // Minimize sits to the LEFT of the close X with a 2-px gap.
        // Same beveled-square chrome as the X; an underscore glyph
        // (a single 7-px row near the bottom of the box) signals
        // "send to the dock" in classic Win9x style.
        if (includeMinimize)
        {
            var min = MinimizeRect(bar);
            RetroSkin.DrawRaised(min);
            DrawMinimizeGlyph(min);
        }

        var close = CloseRect(bar);
        RetroSkin.DrawRaised(close);
        DrawXGlyph(close, 0);
    }

    /// <summary>Returns true if the close (X) button was clicked
    /// this frame. Activities call this in their Update to decide
    /// when to set IsFinished. Minimize is a separate signal — see
    /// <see cref="MinimizeHitTest"/>.</summary>
    public static bool DrawTitleBarHitTest(Rectangle bar, Vector2 mouse, bool leftPressed)
    {
        var close = CloseRect(bar);
        return RetroSkin.PointInRect(mouse, close) && leftPressed;
    }

    /// <summary>Returns true if the minimize button was clicked
    /// this frame. Standalone / sibling-process hosts call
    /// <see cref="Raylib.MinimizeWindow"/> in response; in-process
    /// hosts (the pet's floating widgets, in-pet activities) can
    /// either hide their visible flag or no-op. The button always
    /// renders regardless of whether anything's listening, so the
    /// chrome stays consistent across hosts.</summary>
    public static bool MinimizeHitTest(Rectangle bar, Vector2 mouse, bool leftPressed)
    {
        var min = MinimizeRect(bar);
        return RetroSkin.PointInRect(mouse, min) && leftPressed;
    }

    /// <summary>Rect for the close (X) button.</summary>
    public static Rectangle CloseRect(Rectangle bar)
        => new(bar.X + bar.Width - 17, bar.Y + 2, 15, 14);

    /// <summary>Rect for the minimize button — same size as the
    /// close button, sat 17 px (button width 15 + 2 px gap) to its
    /// left.</summary>
    public static Rectangle MinimizeRect(Rectangle bar)
        => new(bar.X + bar.Width - 17 - 17, bar.Y + 2, 15, 14);

    private static void DrawXGlyph(Rectangle close, int offset)
    {
        // Box is 15×14 — the WIDTH is intentionally odd so the 7-px X glyph
        // can sit on the true center column (cx = X+7) with equal 4-px
        // margins on each side. Trimming the box width to an even number
        // breaks horizontal centering: there's no integer column that
        // splits an even box symmetrically around an odd-width glyph.
        // Y is rounded down (cy = Y+6) since the box height is even (14).
        //
        // Each diagonal is drawn 2 px thick to match the 2-px stroke of
        // the minimize underscore — keeps the two title-bar glyphs at
        // visually equal weight.
        int cx = (int)close.X + 6 + offset;
        int cy = (int)close.Y + 6 + offset;
        for (int i = -3; i <= 3; i++)
        {
            Raylib.DrawPixel(cx + i,     cy + i,     RetroSkin.BodyText);
            Raylib.DrawPixel(cx + i + 1, cy + i,     RetroSkin.BodyText);
            Raylib.DrawPixel(cx + i,     cy - i,     RetroSkin.BodyText);
            Raylib.DrawPixel(cx + i + 1, cy - i,     RetroSkin.BodyText);
        }
    }

    private static void DrawMinimizeGlyph(Rectangle r)
    {
        // 7-px-wide underscore near the bottom of the 15×14 box —
        // matches the Win9x convention (underscore = minimise,
        // dash-in-the-middle = restore, but we don't track restored
        // state in this UI so always show the underscore). Sat one
        // pixel above the very bottom edge for visual breathing
        // room against the button bevel.
        int gx = (int)r.X + 4;
        int gy = (int)r.Y + (int)r.Height - 4;
        for (int i = 0; i < 7; i++)
        {
            Raylib.DrawPixel(gx + i, gy, RetroSkin.BodyText);
            Raylib.DrawPixel(gx + i, gy + 1, RetroSkin.BodyText);
        }
    }

    // ── Pushbutton ───────────────────────────────────────────────────────
    public static void ButtonVisual(Rectangle r, string label, bool pressedLook)
    {
        if (pressedLook) RetroSkin.DrawPressed(r);
        else RetroSkin.DrawRaised(r);

        int tw = RetroSkin.MeasureText(label);
        int tx = (int)(r.X + (r.Width - tw) / 2) + (pressedLook ? 1 : 0);
        int ty = (int)(r.Y + (r.Height - RetroSkin.BodyFontSize) / 2) + (pressedLook ? 1 : 0);
        RetroSkin.DrawText(label, tx, ty, RetroSkin.BodyText);
    }

    public static bool ButtonHitTest(Rectangle r, Vector2 mouse,
                                     bool leftPressed, bool leftReleased, ref bool armed)
    {
        bool hover = RetroSkin.PointInRect(mouse, r);
        if (leftPressed && hover) armed = true;
        bool clicked = false;
        if (leftReleased)
        {
            if (armed && hover) clicked = true;
            armed = false;
        }
        // Update armed look while held outside the rect: stay armed but visually unpressed
        return clicked;
    }

    // ── Menu bar ─────────────────────────────────────────────────────────
    // Per-item horizontal padding around the label. Was +12 (6 px each side),
    // which made clicks feel like they only landed on glyphs; +20 (10 px each
    // side) gives clearly clickable button rectangles.
    private const int MenuItemHPad = 20;

    /// <summary>
    /// Compute the slot rectangle for menu item <paramref name="i"/>. Single
    /// source of truth so visual highlight and hit testing always agree.
    /// Slot fills the full bar height — the previous +2/-4 inset made the
    /// hit area feel tighter than the visible button.
    /// </summary>
    private static Rectangle MenuBarSlot(Rectangle bar, string[] items, int i, out int textX)
    {
        int x = (int)bar.X + 4;
        for (int j = 0; j < i; j++)
            x += RetroSkin.MeasureText(items[j]) + MenuItemHPad;
        int w = RetroSkin.MeasureText(items[i]) + MenuItemHPad;
        textX = x + MenuItemHPad / 2;
        return new Rectangle(x, bar.Y, w, bar.Height);
    }

    // Per-frame menu state tracked across the HitTest → Visual pair so call
    // sites don't have to thread it through. HitTest (called in Update) records
    // hover + a press-flash; Visual (called in Draw) reads them.
    //   - Hover: blue highlight + white text (lights up the moment the cursor
    //     enters the slot — Win98 selected style).
    //   - Click: action fires INSTANTLY on mouse-down (the existing behavior
    //     callers already depend on). The slot also flashes pressed for
    //     ~MenuPressFlashSeconds so you actually see the click register
    //     even on a snap-fast tap-and-release.
    private const float MenuPressFlashSeconds = 0.12f;
    private static int _menuHoverIdx = -1;
    private static int _menuFlashIdx = -1;
    private static double _menuFlashUntil;

    public static void MenuBarVisual(Rectangle bar, string[] items, int hoveredIndex = -1)
    {
        // -1 (the default) defers to the HitTest's recorded hover. Pass a
        // specific index to override (e.g., a programmatically-driven menu).
        int hover = hoveredIndex >= 0 ? hoveredIndex : _menuHoverIdx;
        // Pressed flash latches for a short window after press, so a quick
        // tap-and-release still shows the click visual for a few frames.
        int pressed = (Raylib.GetTime() < _menuFlashUntil) ? _menuFlashIdx : -1;

        Raylib.DrawRectangleRec(bar, RetroSkin.Face);
        Raylib.DrawRectangle((int)bar.X, (int)bar.Y + (int)bar.Height - 1,
            (int)bar.Width, 1, RetroSkin.Shadow);

        for (int i = 0; i < items.Length; i++)
        {
            var slot = MenuBarSlot(bar, items, i, out int textX);
            int textY = (int)slot.Y + ((int)slot.Height - RetroSkin.BodyFontSize) / 2;
            bool isHover = i == hover;
            bool isPressed = i == pressed;
            if (isPressed)
            {
                // Pressed: blue fill + 1px sunken inset border + 1px text
                // nudge down/right so the slot reads as a depressed button.
                Raylib.DrawRectangleRec(slot, RetroSkin.TitleActive);
                int x = (int)slot.X, y = (int)slot.Y, w = (int)slot.Width, h = (int)slot.Height;
                Raylib.DrawRectangle(x, y, w, 1, RetroSkin.DarkShadow);
                Raylib.DrawRectangle(x, y, 1, h, RetroSkin.DarkShadow);
                Raylib.DrawRectangle(x, y + h - 1, w, 1, RetroSkin.Highlight);
                Raylib.DrawRectangle(x + w - 1, y, 1, h, RetroSkin.Highlight);
                RetroSkin.DrawText(items[i], textX + 1, textY + 1, RetroSkin.TitleText);
            }
            else if (isHover)
            {
                Raylib.DrawRectangleRec(slot, RetroSkin.TitleActive);
                RetroSkin.DrawText(items[i], textX, textY, RetroSkin.TitleText);
            }
            else
            {
                RetroSkin.DrawText(items[i], textX, textY, RetroSkin.BodyText);
            }
        }
    }

    /// <summary>
    /// Hit-test the menu bar. Records hover for the matching
    /// <see cref="MenuBarVisual"/> call. Click fires INSTANTLY on press
    /// (returns the clicked index that frame) and also records a brief
    /// press-flash so the visual shows feedback even for snap-fast clicks.
    /// </summary>
    public static int MenuBarHitTest(Rectangle bar, string[] items, Vector2 mouse, bool leftPressed)
    {
        int hover = -1;
        for (int i = 0; i < items.Length; i++)
        {
            var slot = MenuBarSlot(bar, items, i, out _);
            if (RetroSkin.PointInRect(mouse, slot)) { hover = i; break; }
        }
        _menuHoverIdx = hover;

        if (leftPressed && hover >= 0)
        {
            _menuFlashIdx = hover;
            _menuFlashUntil = Raylib.GetTime() + MenuPressFlashSeconds;
            return hover;
        }
        return -1;
    }

    // ── Status bar ───────────────────────────────────────────────────────
    public static void StatusBar(Rectangle bar, params string[] panels)
    {
        Raylib.DrawRectangleRec(bar, RetroSkin.Face);
        if (panels.Length == 0) return;
        int panelW = (int)bar.Width / panels.Length;
        int fontSize = StatusFontSize;
        for (int i = 0; i < panels.Length; i++)
        {
            var slot = new Rectangle(bar.X + i * panelW + 2, bar.Y + 2,
                panelW - 4, bar.Height - 4);
            RetroSkin.DrawSunken(slot, RetroSkin.Face);

            // Truncate to fit the slot's text area. (Scissor mode would clip
            // mid-glyph bleed too, but on Retina macOS Raylib's scissor uses
            // framebuffer pixels not logical pixels, which clips everything to
            // the wrong rect — so we rely on truncation alone.)
            int textArea = (int)slot.Width - 8;
            string text = TruncateToWidth(panels[i], textArea, fontSize);
            int ty = (int)(slot.Y + (slot.Height - fontSize) / 2);
            RetroSkin.DrawText(text, (int)slot.X + 4, ty, RetroSkin.BodyText, fontSize);
        }
    }

    /// <summary>Trim a string to fit in maxWidth pixels at the given font size, with an ellipsis if truncation happened.</summary>
    public static string TruncateToWidth(string text, int maxWidth, int fontSize)
    {
        if (RetroSkin.MeasureText(text, fontSize) <= maxWidth) return text;
        const string ell = "…";
        int ellW = RetroSkin.MeasureText(ell, fontSize);
        for (int len = text.Length - 1; len > 0; len--)
        {
            string candidate = text[..len];
            if (RetroSkin.MeasureText(candidate, fontSize) + ellW <= maxWidth)
                return candidate + ell;
        }
        return ell;
    }

    // ── Group box ────────────────────────────────────────────────────────
    public static void GroupBox(Rectangle r, string label)
    {
        int x = (int)r.X, y = (int)r.Y, w = (int)r.Width, h = (int)r.Height;
        Raylib.DrawRectangleLines(x, y + 6, w, h - 6, RetroSkin.Shadow);
        Raylib.DrawRectangleLines(x + 1, y + 7, w, h - 6, RetroSkin.Highlight);
        int tw = RetroSkin.MeasureText(label) + 4;
        Raylib.DrawRectangle(x + 8, y, tw, 12, RetroSkin.Face);
        RetroSkin.DrawText(label, x + 10, y, RetroSkin.BodyText);
    }

    // ── Checkbox ─────────────────────────────────────────────────────────
    public static void CheckboxVisual(Vector2 pos, string label, bool value)
    {
        var box = new Rectangle(pos.X, pos.Y, 13, 13);
        RetroSkin.DrawSunken(box);
        if (value)
        {
            int bx = (int)box.X, by = (int)box.Y;
            Raylib.DrawLine(bx + 3, by + 6, bx + 5, by + 8, RetroSkin.BodyText);
            Raylib.DrawLine(bx + 4, by + 6, bx + 6, by + 8, RetroSkin.BodyText);
            Raylib.DrawLine(bx + 5, by + 8, bx + 10, by + 3, RetroSkin.BodyText);
            Raylib.DrawLine(bx + 6, by + 8, bx + 11, by + 3, RetroSkin.BodyText);
        }
        RetroSkin.DrawText(label, (int)pos.X + 18, (int)pos.Y - 1, RetroSkin.BodyText);
    }

    public static bool CheckboxHitTest(Vector2 pos, string label, bool value, Vector2 mouse,
                                       bool leftReleased, ref bool armed)
    {
        var hit = new Rectangle(pos.X, pos.Y, 13 + 4 + RetroSkin.MeasureText(label), 14);
        bool hover = RetroSkin.PointInRect(mouse, hit);
        if (Raylib.IsMouseButtonPressed(MouseButton.Left) && hover) armed = true;
        if (leftReleased)
        {
            bool wasArmed = armed;
            armed = false;
            if (wasArmed && hover) return !value;
        }
        return value;
    }
}
