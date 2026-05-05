using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// FreeCell — 8 tableau columns dealt 7/7/7/7/6/6/6/6, 4 free cells, 4
/// foundations. Tableau builds down alternating colors; foundations build
/// up by suit. Click to select source, click destination to move. Moving a
/// run is allowed up to (free_cells + 1) * 2^(empty_columns) cards.
/// </summary>
public class FreeCellActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Cols = 8;
    private const int Margin = 16;
    private const int Spacing = 8;
    private const int TopRowGap = 16;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + Cols * CardKit.CardW + (Cols - 1) * Spacing,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + CardKit.CardH + TopRowGap
            + 14 * CardKit.CascadeY + CardKit.CardH
            + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly List<Card>[] _tableau = new List<Card>[Cols];
    private readonly Card?[] _free = new Card?[4];
    private readonly List<Card>[] _foundations = new List<Card>[4];
    private bool _won;
    private readonly Random _rng = new();

    // Selection: (kind, index, depth-from-top for tableau)
    // kind: "tab" / "free" / "found"
    private (string kind, int idx, int depth)? _sel;

    public void Load() { for (int i = 0; i < 4; i++) _foundations[i] = new(); Deal(); }

    private void Deal()
    {
        for (int i = 0; i < Cols; i++) _tableau[i] = new();
        for (int i = 0; i < 4; i++) { _free[i] = null; _foundations[i].Clear(); }
        _won = false; _sel = null;

        var deck = CardKit.NewDeck();
        CardKit.Shuffle(deck, _rng);
        foreach (var c in deck) c.FaceUp = true;
        for (int i = 0; i < deck.Count; i++)
            _tableau[i % Cols].Add(deck[i]);
    }

    private Vector2 FreePos(int i)
    {
        float x = FrameInset + Margin + i * (CardKit.CardW + Spacing);
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        return new Vector2(x, y);
    }

    private Vector2 FoundationPos(int i)
    {
        float x = FrameInset + Margin + (4 + i) * (CardKit.CardW + Spacing);
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        return new Vector2(x, y);
    }

    private Vector2 ColPos(int c)
    {
        float x = FrameInset + Margin + c * (CardKit.CardW + Spacing);
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + CardKit.CardH + TopRowGap;
        return new Vector2(x, y);
    }

    private int FreeCount() { int n = 0; for (int i = 0; i < 4; i++) if (_free[i] == null) n++; return n; }
    private int EmptyCols(int excludeIdx = -1)
    {
        int n = 0;
        for (int i = 0; i < Cols; i++) if (i != excludeIdx && _tableau[i].Count == 0) n++;
        return n;
    }
    private int MaxRun(int excludeDestCol = -1)
        => (FreeCount() + 1) * (1 << EmptyCols(excludeDestCol));

    private static bool ColorsAlternate(Card a, Card b) => a.IsRed != b.IsRed;

    private bool RunValid(List<Card> source, int startDepth)
    {
        for (int i = startDepth; i < source.Count - 1; i++)
        {
            var a = source[i];
            var b = source[i + 1];
            if (a.Rank - 1 != b.Rank) return false;
            if (!ColorsAlternate(a, b)) return false;
        }
        return true;
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;

        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        if (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New" }, local, leftPressed) == 0)
        { Deal(); return; }

        if (!leftPressed || _won) return;

        // Free cells
        for (int i = 0; i < 4; i++)
            if (RetroSkin.PointInRect(local, new Rectangle(FreePos(i).X, FreePos(i).Y, CardKit.CardW, CardKit.CardH)))
            { ClickFree(i); return; }

        // Foundations
        for (int i = 0; i < 4; i++)
            if (RetroSkin.PointInRect(local, new Rectangle(FoundationPos(i).X, FoundationPos(i).Y, CardKit.CardW, CardKit.CardH)))
            { ClickFoundation(i); return; }

        // Tableau
        for (int c = 0; c < Cols; c++)
        {
            var basePos = ColPos(c);
            int n = _tableau[c].Count;
            if (n == 0)
            {
                var rect = new Rectangle(basePos.X, basePos.Y, CardKit.CardW, CardKit.CardH);
                if (RetroSkin.PointInRect(local, rect)) { ClickTableau(c, 0); return; }
                continue;
            }
            for (int i = n - 1; i >= 0; i--)
            {
                var p = basePos + new Vector2(0, i * CardKit.CascadeY);
                float clickH = (i == n - 1) ? CardKit.CardH : CardKit.CascadeY;
                var rect = new Rectangle(p.X, p.Y, CardKit.CardW, clickH);
                if (RetroSkin.PointInRect(local, rect)) { ClickTableau(c, i); return; }
            }
        }

        _sel = null;
    }

    private void ClickFree(int i)
    {
        if (_sel == null)
        {
            if (_free[i] != null) _sel = ("free", i, 0);
            return;
        }
        // Move selected → free cell (single card only)
        if (_free[i] == null && TryTakeSingle(out var card))
        {
            _free[i] = card;
            _sel = null;
        }
        else _sel = null;
    }

    private void ClickFoundation(int i)
    {
        if (_sel == null) return;
        if (!TryTakeSingle(out var card, peek: true)) { _sel = null; return; }
        int needRank = _foundations[i].Count + 1;
        bool valid = card.Rank == needRank
            && (_foundations[i].Count == 0 ? true : _foundations[i][^1].Suit == card.Suit);
        if (!valid) { _sel = null; return; }
        TryTakeSingle(out _);
        _foundations[i].Add(card);
        CheckWin();
        _sel = null;
    }

    private void ClickTableau(int c, int depth)
    {
        if (_sel == null)
        {
            if (_tableau[c].Count == 0) return;
            if (!RunValid(_tableau[c], depth)) return;
            _sel = ("tab", c, depth);
            return;
        }

        // Determine destination top card
        var dest = _tableau[c];
        Card? destTop = dest.Count > 0 ? dest[^1] : null;

        // Get selected run
        var (kind, idx, sd) = _sel.Value;
        int runLen = kind == "tab" ? _tableau[idx].Count - sd : 1;
        // Trying to move into same column? cancel
        if (kind == "tab" && idx == c) { _sel = null; return; }

        // Validate destination compatibility
        if (destTop != null)
        {
            Card top = kind == "tab" ? _tableau[idx][sd]
                     : kind == "free" ? _free[idx]!
                     : _foundations[idx][^1];
            if (destTop.Rank - 1 != top.Rank) { _sel = null; return; }
            if (!ColorsAlternate(destTop, top)) { _sel = null; return; }
        }

        // Validate run size
        if (runLen > MaxRun(c)) { _sel = null; return; }

        // Execute
        if (kind == "tab")
        {
            var moving = _tableau[idx].GetRange(sd, runLen);
            _tableau[idx].RemoveRange(sd, runLen);
            dest.AddRange(moving);
        }
        else if (kind == "free" && _free[idx] != null)
        {
            dest.Add(_free[idx]!);
            _free[idx] = null;
        }
        _sel = null;
    }

    /// <summary>Read or pop a single source card. peek=true reads without removing.</summary>
    private bool TryTakeSingle(out Card card, bool peek = false)
    {
        card = null!;
        if (_sel == null) return false;
        var (kind, idx, sd) = _sel.Value;
        if (kind == "tab")
        {
            int len = _tableau[idx].Count - sd;
            if (len != 1) return false;
            card = _tableau[idx][sd];
            if (!peek) _tableau[idx].RemoveAt(sd);
            return true;
        }
        if (kind == "free" && _free[idx] != null)
        {
            card = _free[idx]!;
            if (!peek) _free[idx] = null;
            return true;
        }
        return false;
    }

    private void CheckWin()
    {
        for (int i = 0; i < 4; i++)
            if (_foundations[i].Count != 13) return;
        _won = true;
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "FreeCell", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New" }, -1);

        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        float bodyH = PanelSize.Y - bodyY - FrameInset - RetroWidgets.StatusBarHeight;
        Raylib.DrawRectangleRec(new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + bodyY,
            PanelSize.X - 2 * FrameInset, bodyH), new Color(8, 96, 56, 255));

        // Free cells + foundations (top row)
        for (int i = 0; i < 4; i++)
        {
            var p = FreePos(i);
            var abs = new Vector2(panelOffset.X + p.X, panelOffset.Y + p.Y);
            if (_free[i] == null) CardKit.DrawEmptySlot(abs);
            else CardKit.DrawCard(_free[i]!, abs);
            if (_sel?.kind == "free" && _sel?.idx == i)
                Raylib.DrawRectangleLinesEx(new Rectangle(abs.X - 2, abs.Y - 2, CardKit.CardW + 4, CardKit.CardH + 4), 2, new Color(255, 220, 0, 255));
        }
        for (int i = 0; i < 4; i++)
        {
            var p = FoundationPos(i);
            var abs = new Vector2(panelOffset.X + p.X, panelOffset.Y + p.Y);
            if (_foundations[i].Count == 0) CardKit.DrawEmptySlot(abs);
            else CardKit.DrawCard(_foundations[i][^1], abs);
        }

        // Tableau
        for (int c = 0; c < Cols; c++)
        {
            var basePos = ColPos(c);
            var abs = new Vector2(panelOffset.X + basePos.X, panelOffset.Y + basePos.Y);
            if (_tableau[c].Count == 0) { CardKit.DrawEmptySlot(abs); continue; }
            for (int i = 0; i < _tableau[c].Count; i++)
            {
                var p = abs + new Vector2(0, i * CardKit.CascadeY);
                CardKit.DrawCard(_tableau[c][i], p);
                if (_sel?.kind == "tab" && _sel?.idx == c && i >= _sel?.depth)
                {
                    float h = i == _tableau[c].Count - 1 ? CardKit.CardH : CardKit.CascadeY;
                    Raylib.DrawRectangleLinesEx(new Rectangle(p.X - 1, p.Y - 1, CardKit.CardW + 2, h + 2), 2, new Color(255, 220, 0, 255));
                }
            }
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        int found = 0; for (int i = 0; i < 4; i++) found += _foundations[i].Count;
        string state = _won ? "You win!" : "Click source, then destination";
        RetroWidgets.StatusBar(status, state, $"Foundations: {found}/52   Free: {FreeCount()}/4");
    }

    public void Close() { }
}
