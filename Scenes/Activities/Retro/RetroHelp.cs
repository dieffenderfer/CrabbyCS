using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Scenes.Activities.Retro;

/// <summary>
/// Shared in-window Help overlay. Each game owns one of these, sets the title
/// and lines once, and routes input/draw through it. An optional Diagram
/// callback paints into the reserved illustration strip at the top.
/// </summary>
public class RetroHelp
{
    public string Title = "How to play";
    public string[] Lines = Array.Empty<string>();
    public Action<Rectangle>? Diagram;
    public int DiagramHeight;
    public bool Visible;

    private const int FontSize = 14;
    private const int LineGap = 18;
    private const int Padding = 14;

    public Rectangle PanelRectLocal(Vector2 panelSize)
    {
        float w = Math.Min(panelSize.X - 40, 380);
        int textHeight = Lines.Length * LineGap;
        float h = RetroWidgets.TitleBarHeight + 2 * Padding + textHeight + DiagramHeight + 24;
        h = Math.Min(h, panelSize.Y - 40);
        return new Rectangle((panelSize.X - w) / 2, (panelSize.Y - h) / 2, w, h);
    }

    /// <summary>
    /// Returns true if the overlay consumed input this frame (caller should
    /// skip its own click handling).
    /// </summary>
    public bool HandleInput(Vector2 panelLocalMouse, bool leftPressed, Vector2 panelSize)
    {
        if (!Visible) return false;
        if (leftPressed && !RetroSkin.PointInRect(panelLocalMouse, PanelRectLocal(panelSize)))
            Visible = false;
        return true;
    }

    public void Draw(Vector2 panelOffset, Vector2 panelSize)
    {
        if (!Visible) return;
        var r = PanelRectLocal(panelSize);
        var abs = new Rectangle(panelOffset.X + r.X, panelOffset.Y + r.Y, r.Width, r.Height);
        Raylib.DrawRectangle((int)abs.X + 4, (int)abs.Y + 4, (int)abs.Width, (int)abs.Height,
            new Color(0, 0, 0, 100));
        RetroSkin.DrawRaised(abs);

        var titleBar = new Rectangle(abs.X + 3, abs.Y + 3, abs.Width - 6, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, Title, true);

        int y = (int)abs.Y + 3 + RetroWidgets.TitleBarHeight + Padding;
        if (Diagram != null && DiagramHeight > 0)
        {
            var diag = new Rectangle(abs.X + Padding, y, abs.Width - 2 * Padding, DiagramHeight);
            Diagram(diag);
            y += DiagramHeight + 8;
        }

        foreach (var line in Lines)
        {
            RetroSkin.DrawText(line, (int)abs.X + Padding, y, RetroSkin.BodyText, FontSize);
            y += LineGap;
        }

        RetroSkin.DrawText("Click outside to dismiss",
            (int)abs.X + Padding, (int)(abs.Y + abs.Height - 18),
            RetroSkin.DisabledText, 12);
    }
}
