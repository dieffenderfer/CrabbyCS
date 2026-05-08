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
    public static void DrawTitleBarVisual(Rectangle bar, string title, bool active, int titleYOffset = 0)
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

        var close = CloseRect(bar);
        RetroSkin.DrawRaised(close);
        DrawXGlyph(close, 0);
    }

    /// <summary>Returns true if the close button was clicked this frame.</summary>
    public static bool DrawTitleBarHitTest(Rectangle bar, Vector2 mouse, bool leftPressed)
    {
        var close = CloseRect(bar);
        return RetroSkin.PointInRect(mouse, close) && leftPressed;
    }

    private static Rectangle CloseRect(Rectangle bar)
        => new(bar.X + bar.Width - 18, bar.Y + 2, 16, 14);

    private static void DrawXGlyph(Rectangle close, int offset)
    {
        // Box is 16×14 (even both ways). Y is rounded down (cy = Y+6) so the
        // X reads as visually centered vertically; X uses the upper half of
        // the box's two-pixel center column (cx = X+8) — the user prefers
        // the slight rightward bias to balance the chrome's left highlight.
        int cx = (int)close.X + 8 + offset;
        int cy = (int)close.Y + 6 + offset;
        for (int i = -3; i <= 3; i++)
        {
            Raylib.DrawPixel(cx + i, cy + i, RetroSkin.BodyText);
            Raylib.DrawPixel(cx + i, cy - i, RetroSkin.BodyText);
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

    public static void MenuBarVisual(Rectangle bar, string[] items, int hoveredIndex)
    {
        Raylib.DrawRectangleRec(bar, RetroSkin.Face);
        Raylib.DrawRectangle((int)bar.X, (int)bar.Y + (int)bar.Height - 1,
            (int)bar.Width, 1, RetroSkin.Shadow);

        for (int i = 0; i < items.Length; i++)
        {
            var slot = MenuBarSlot(bar, items, i, out int textX);
            int textY = (int)slot.Y + ((int)slot.Height - RetroSkin.BodyFontSize) / 2;
            if (hoveredIndex == i)
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

    /// <summary>Returns the index of the clicked menu item this frame, or -1.</summary>
    public static int MenuBarHitTest(Rectangle bar, string[] items, Vector2 mouse, bool leftPressed)
    {
        if (!leftPressed) return -1;
        for (int i = 0; i < items.Length; i++)
        {
            var slot = MenuBarSlot(bar, items, i, out _);
            if (RetroSkin.PointInRect(mouse, slot)) return i;
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
