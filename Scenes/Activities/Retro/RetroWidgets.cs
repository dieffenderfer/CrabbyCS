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
    public const int StatusBarHeight = 20;
    public const int BorderThickness = 3;

    public static void DrawWindowFrame(Rectangle panel) => RetroSkin.DrawRaised(panel);

    // ── Title bar ────────────────────────────────────────────────────────
    public static void DrawTitleBarVisual(Rectangle bar, string title, bool active)
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
        RetroSkin.DrawText(title, (int)bar.X + 4, (int)bar.Y + 2,
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
        int cx = (int)close.X + 8 + offset;
        int cy = (int)close.Y + 7 + offset;
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
    public static void MenuBarVisual(Rectangle bar, string[] items, int hoveredIndex)
    {
        Raylib.DrawRectangleRec(bar, RetroSkin.Face);
        Raylib.DrawRectangle((int)bar.X, (int)bar.Y + (int)bar.Height - 1,
            (int)bar.Width, 1, RetroSkin.Shadow);

        int x = (int)bar.X + 4;
        for (int i = 0; i < items.Length; i++)
        {
            int w = RetroSkin.MeasureText(items[i]) + 12;
            var slot = new Rectangle(x, bar.Y + 2, w, bar.Height - 4);
            if (hoveredIndex == i)
            {
                Raylib.DrawRectangleRec(slot, RetroSkin.TitleActive);
                RetroSkin.DrawText(items[i], x + 6, (int)slot.Y + 3, RetroSkin.TitleText);
            }
            else
            {
                RetroSkin.DrawText(items[i], x + 6, (int)slot.Y + 3, RetroSkin.BodyText);
            }
            x += w;
        }
    }

    /// <summary>Returns the index of the clicked menu item this frame, or -1.</summary>
    public static int MenuBarHitTest(Rectangle bar, string[] items, Vector2 mouse, bool leftPressed)
    {
        int x = (int)bar.X + 4;
        for (int i = 0; i < items.Length; i++)
        {
            int w = RetroSkin.MeasureText(items[i]) + 12;
            var slot = new Rectangle(x, bar.Y + 2, w, bar.Height - 4);
            if (RetroSkin.PointInRect(mouse, slot) && leftPressed) return i;
            x += w;
        }
        return -1;
    }

    // ── Status bar ───────────────────────────────────────────────────────
    public static void StatusBar(Rectangle bar, params string[] panels)
    {
        Raylib.DrawRectangleRec(bar, RetroSkin.Face);
        if (panels.Length == 0) return;
        int panelW = (int)bar.Width / panels.Length;
        for (int i = 0; i < panels.Length; i++)
        {
            var slot = new Rectangle(bar.X + i * panelW + 2, bar.Y + 2,
                panelW - 4, bar.Height - 4);
            RetroSkin.DrawSunken(slot, RetroSkin.Face);
            RetroSkin.DrawText(panels[i], (int)slot.X + 4, (int)slot.Y + 3, RetroSkin.BodyText);
        }
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
