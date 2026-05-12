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
    // Help popup hugs the parent window's width minus a small inset so
    // shadows still show. Previous behavior capped at 380px which made
    // the popup look noticeably thinner than wider parent panels.
    private const int ParentInset = 12;

    public Rectangle PanelRectLocal(Vector2 panelSize)
    {
        float w = Math.Max(panelSize.X - ParentInset, 200);
        if (w > panelSize.X) w = panelSize.X;
        int textHeight = WrapLines(Lines, (int)w - 2 * Padding).Count * LineGap;
        float h = RetroWidgets.TitleBarHeight + 2 * Padding + textHeight + DiagramHeight;
        h = Math.Min(h, panelSize.Y - 20);
        return new Rectangle((panelSize.X - w) / 2, (panelSize.Y - h) / 2, w, h);
    }

    /// <summary>
    /// Returns true if the overlay consumed input this frame (caller should
    /// skip its own click handling).
    /// </summary>
    public bool HandleInput(Vector2 panelLocalMouse, bool leftPressed, Vector2 panelSize)
    {
        if (!Visible) return false;
        var rect = PanelRectLocal(panelSize);
        var titleBar = new Rectangle(rect.X + 3, rect.Y + 3,
            rect.Width - 6, RetroWidgets.TitleBarHeight);
        var close = RetroWidgets.CloseRect(titleBar);
        if (leftPressed && RetroSkin.PointInRect(panelLocalMouse, close))
        {
            Visible = false;
            return true;
        }
        if (leftPressed && !RetroSkin.PointInRect(panelLocalMouse, rect))
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
        RetroWidgets.DrawTitleBarVisual(titleBar, Title, true, includeMinimize: false);

        int y = (int)abs.Y + 3 + RetroWidgets.TitleBarHeight + Padding;
        if (Diagram != null && DiagramHeight > 0)
        {
            var diag = new Rectangle(abs.X + Padding, y, abs.Width - 2 * Padding, DiagramHeight);
            Diagram(diag);
            y += DiagramHeight + 8;
        }

        int wrapWidth = (int)abs.Width - 2 * Padding;
        foreach (var line in WrapLines(Lines, wrapWidth))
        {
            RetroSkin.DrawText(line, (int)abs.X + Padding, y, RetroSkin.BodyText, FontSize);
            y += LineGap;
        }
    }

    /// <summary>
    /// Word-wrap each source line to fit <paramref name="maxWidth"/> pixels.
    /// Source lines are already author-broken; this pass only kicks in when
    /// the popup is narrower than an individual line, which used to bleed
    /// past the right edge.
    /// </summary>
    private static List<string> WrapLines(string[] src, int maxWidth)
    {
        var outLines = new List<string>(src.Length);
        if (maxWidth <= 0)
        {
            outLines.AddRange(src);
            return outLines;
        }
        foreach (var line in src)
        {
            if (RetroSkin.MeasureText(line, FontSize) <= maxWidth)
            {
                outLines.Add(line);
                continue;
            }
            var words = line.Split(' ');
            string cur = "";
            foreach (var w in words)
            {
                string candidate = cur.Length == 0 ? w : cur + " " + w;
                if (RetroSkin.MeasureText(candidate, FontSize) <= maxWidth)
                {
                    cur = candidate;
                }
                else
                {
                    if (cur.Length > 0) outLines.Add(cur);
                    cur = w;
                }
            }
            if (cur.Length > 0) outLines.Add(cur);
        }
        return outLines;
    }
}
