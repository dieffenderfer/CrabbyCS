using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Fuji Golf — a procedurally-generated 9-hole top-down course. Drag from the
/// ball to set direction and power, release to swing. Terrain (fairway, rough,
/// sand, water, green) controls friction; trees bounce the ball; the cup ends
/// the hole. Strokes per hole, par, and a running total are tracked on a side
/// scorecard.
/// </summary>
public class FujiGolfActivity : IActivity
{
    private const int FrameInset = 3;
    private const int CanvasW = 540;
    private const int CanvasH = 360;
    private const int ScoreW = 160;
    private const int Margin = 12;
    private const int Holes = 9;

    public Vector2 PanelSize => new(
        2 * FrameInset + CanvasW + ScoreW + Margin,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + CanvasH + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private record class HoleLayout(
        Vector2 Tee,
        Vector2 Cup,
        int Par,
        List<Vector2> Trees,
        List<(Vector2 Center, float Rx, float Ry, int Kind)> Hazards); // Kind: 0 sand, 1 water

    private List<HoleLayout> _course = new();
    private int[] _strokes = new int[Holes];
    private int _holeIdx;
    private Vector2 _ball;
    private Vector2 _vel;
    private bool _aiming;
    private Vector2 _aimEnd;
    private bool _holeComplete;
    private bool _roundComplete;
    private float _holeFlashTimer;
    private List<Vector2> _trail = new();
    private float _trailDropTimer;
    private readonly Random _rng = new();

    private readonly RetroHelp _help = new()
    {
        Title = "Fuji Golf — How to play",
        Lines = new[]
        {
            "Sink the ball in nine holes with as few strokes as possible.",
            "Drag from the ball to aim, longer drag = more power, release to hit.",
            "Trees bounce, water costs a penalty stroke and resets you to the tee.",
            "Sand bunkers slow the ball heavily; the green is fast and slick.",
            "Holed when the ball passes near the cup with low speed.",
            "Beat par across all nine holes for a clean round.",
        },
        DiagramHeight = 64,
        Diagram = r =>
        {
            // A miniature hole: tee (white), green (lighter green), cup (black)
            // with flag, plus a tree and a water hazard.
            var bg = new Color(64, 144, 64, 255);
            Raylib.DrawRectangleRec(r, bg);
            Raylib.DrawRectangleLines((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height,
                new Color(40, 96, 32, 255));
            // Tee
            Raylib.DrawCircle((int)r.X + 16, (int)(r.Y + r.Height / 2), 4,
                new Color(255, 255, 255, 255));
            // Tree
            Raylib.DrawCircle((int)(r.X + r.Width * 0.45f), (int)(r.Y + r.Height / 2 - 4),
                7, new Color(40, 120, 60, 255));
            Raylib.DrawRectangle((int)(r.X + r.Width * 0.45f) - 1,
                (int)(r.Y + r.Height / 2 + 3), 2, 4, new Color(80, 56, 32, 255));
            // Water
            Raylib.DrawCircle((int)(r.X + r.Width * 0.65f), (int)(r.Y + r.Height / 2 + 8),
                10, new Color(48, 96, 192, 255));
            // Green
            Raylib.DrawCircle((int)(r.X + r.Width - 30), (int)(r.Y + r.Height / 2),
                14, new Color(96, 184, 80, 255));
            // Cup + flag
            int cx = (int)(r.X + r.Width - 30);
            int cy = (int)(r.Y + r.Height / 2);
            Raylib.DrawCircle(cx, cy, 3, RetroSkin.BodyText);
            Raylib.DrawRectangle(cx - 1, cy - 16, 2, 16, RetroSkin.BodyText);
            Raylib.DrawTriangle(
                new Vector2(cx + 1, cy - 16),
                new Vector2(cx + 10, cy - 13),
                new Vector2(cx + 1, cy - 10),
                new Color(220, 60, 60, 255));
        },
    };

    public void Load() => StartRound();

    private void StartRound()
    {
        _course.Clear();
        for (int i = 0; i < Holes; i++) _course.Add(GenerateHole(i));
        _strokes = new int[Holes];
        _holeIdx = 0;
        _holeComplete = false;
        _roundComplete = false;
        _trail.Clear();
        ResetBall();
    }

    private void ResetBall()
    {
        _ball = _course[_holeIdx].Tee;
        _vel = Vector2.Zero;
        _aiming = false;
        _holeFlashTimer = 0;
        _trail.Clear();
    }

    private HoleLayout GenerateHole(int idx)
    {
        // Vary par 3..5 across the round; difficulty rises with index.
        int par = idx switch { 0 => 3, 1 => 3, 2 => 4, 3 => 4, 4 => 4, 5 => 5, 6 => 4, 7 => 5, _ => 5 };

        // Tee on the left, cup on the right, both with vertical jitter.
        var tee = new Vector2(40, 60 + _rng.Next(CanvasH - 120));
        var cup = new Vector2(CanvasW - 40, 60 + _rng.Next(CanvasH - 120));

        var trees = new List<Vector2>();
        int treeCount = 2 + _rng.Next(par);
        int safety = 0;
        while (trees.Count < treeCount && safety++ < 200)
        {
            var t = new Vector2(
                100 + _rng.Next(CanvasW - 200),
                30 + _rng.Next(CanvasH - 60));
            if (Vector2.Distance(t, tee) < 50) continue;
            if (Vector2.Distance(t, cup) < 50) continue;
            bool overlap = false;
            foreach (var u in trees) if (Vector2.Distance(t, u) < 28) { overlap = true; break; }
            if (overlap) continue;
            trees.Add(t);
        }

        var hazards = new List<(Vector2, float, float, int)>();
        // 1-2 hazards per hole, mixed
        int hzCount = par == 3 ? 1 : 2;
        for (int h = 0; h < hzCount; h++)
        {
            int kind = _rng.Next(2);
            var center = new Vector2(
                CanvasW / 4f + _rng.Next(CanvasW / 2),
                40 + _rng.Next(CanvasH - 80));
            // Don't drop a hazard right on the tee or the cup
            if (Vector2.Distance(center, tee) < 60) center.X += 80;
            if (Vector2.Distance(center, cup) < 60) center.X -= 80;
            float rx = 24 + _rng.Next(20);
            float ry = 14 + _rng.Next(14);
            hazards.Add((center, rx, ry, kind));
        }

        return new HoleLayout(tee, cup, par, trees, hazards);
    }

    private int Terrain(Vector2 p)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= CanvasW || p.Y >= CanvasH) return 1;
        var hole = _course[_holeIdx];
        foreach (var (c, rx, ry, kind) in hole.Hazards)
        {
            float dx = p.X - c.X, dy = p.Y - c.Y;
            if ((dx * dx) / (rx * rx) + (dy * dy) / (ry * ry) < 1f)
                return kind == 0 ? 2 : 3; // 2 sand, 3 water
        }
        if (Vector2.Distance(p, hole.Cup) < 40) return 4; // green
        if (p.X < 18 || p.Y < 18 || p.X > CanvasW - 18 || p.Y > CanvasH - 18) return 1; // rough
        return 0; // fairway
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
        switch (RetroWidgets.MenuBarHitTest(menuBar,
            new[] { "New Round", "Replay Hole", "Skip", "Help" }, local, leftPressed))
        {
            case 0: StartRound(); return;
            case 1: ResetBall(); return;
            case 2: AdvanceHole(); return;
            case 3: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (_roundComplete) return;

        var canvasOrigin = new Vector2(FrameInset, FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight);
        var canvasMouse = local - canvasOrigin;

        // Hole-completed pause then advance
        if (_holeComplete)
        {
            _holeFlashTimer += delta;
            if (_holeFlashTimer > 1.4f) AdvanceHole();
            return;
        }

        // Ball motion
        if (_vel.LengthSquared() > 0.01f)
        {
            _ball += _vel * delta;
            int t = Terrain(_ball);
            float fric = t switch { 2 => 5.5f, 1 => 2.6f, 4 => 0.8f, _ => 1.4f };
            _vel *= MathF.Max(0, 1f - fric * delta);

            // Trail
            _trailDropTimer += delta;
            if (_trailDropTimer > 0.04f)
            {
                _trailDropTimer = 0;
                _trail.Add(_ball);
                if (_trail.Count > 30) _trail.RemoveAt(0);
            }

            // Wall bounce
            if (_ball.X < 6 || _ball.X > CanvasW - 6) { _vel.X = -_vel.X * 0.55f; _ball.X = Math.Clamp(_ball.X, 6, CanvasW - 6); }
            if (_ball.Y < 6 || _ball.Y > CanvasH - 6) { _vel.Y = -_vel.Y * 0.55f; _ball.Y = Math.Clamp(_ball.Y, 6, CanvasH - 6); }

            // Tree bounces
            foreach (var tree in _course[_holeIdx].Trees)
            {
                var diff = _ball - tree;
                float treeR = 12;
                float ballR = 5;
                if (diff.LengthSquared() < (treeR + ballR) * (treeR + ballR))
                {
                    var n = Vector2.Normalize(diff);
                    _vel = Vector2.Reflect(_vel, n) * 0.7f;
                    _ball = tree + n * (treeR + ballR + 0.5f);
                }
            }

            // Water = penalty drop back to tee
            if (Terrain(_ball) == 3)
            {
                _ball = _course[_holeIdx].Tee;
                _vel = Vector2.Zero;
                _strokes[_holeIdx]++;
                _trail.Clear();
            }

            // Hole detection
            if (Vector2.Distance(_ball, _course[_holeIdx].Cup) < 8 && _vel.Length() < 90f)
            {
                _ball = _course[_holeIdx].Cup;
                _vel = Vector2.Zero;
                _holeComplete = true;
                _holeFlashTimer = 0;
            }
            return;
        }

        // Aiming with the ball at rest
        if (leftPressed && (canvasMouse - _ball).LengthSquared() < 14 * 14)
            _aiming = true;
        if (_aiming) _aimEnd = canvasMouse;
        if (leftReleased && _aiming)
        {
            _aiming = false;
            var dir = _ball - _aimEnd;
            float power = Math.Min(dir.Length() * 4f, 380f);
            if (power < 12) return;
            _vel = Vector2.Normalize(dir) * power;
            _strokes[_holeIdx]++;
            _trail.Clear();
        }
    }

    private void AdvanceHole()
    {
        _holeFlashTimer = 0;
        _holeComplete = false;
        _trail.Clear();
        if (_holeIdx >= Holes - 1)
        {
            _roundComplete = true;
            return;
        }
        _holeIdx++;
        ResetBall();
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, $"Fuji Golf — Hole {_holeIdx + 1} of {Holes}", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New Round", "Replay Hole", "Skip", "Help" }, -1);

        var canvasOrigin = new Vector2(
            panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight);
        var canvas = new Rectangle(canvasOrigin.X, canvasOrigin.Y, CanvasW, CanvasH);

        DrawCourse(canvasOrigin, canvas);
        DrawScorecard(panelOffset);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);

        int total = 0; for (int i = 0; i < Holes; i++) total += _strokes[i];
        int parTotal = 0; for (int i = 0; i < Holes; i++) parTotal += _course[i].Par;

        string state;
        if (_roundComplete)
        {
            int rel = total - parTotal;
            state = rel == 0 ? $"Round done — even par ({total})"
                  : rel < 0 ? $"Round done — {rel} under par ({total})"
                            : $"Round done — +{rel} over par ({total})";
        }
        else if (_holeComplete) state = $"Holed in {_strokes[_holeIdx]}!";
        else if (_aiming) state = "Drag to aim, release to swing";
        else state = "Drag from ball to aim";

        RetroWidgets.StatusBar(status, state, $"H{_holeIdx + 1}  Par {_course[_holeIdx].Par}  Strokes {_strokes[_holeIdx]}");

        _help.Draw(panelOffset, PanelSize);
    }

    private void DrawCourse(Vector2 canvasOrigin, Rectangle canvas)
    {
        // Fairway base
        Raylib.DrawRectangleRec(canvas, new Color(64, 144, 64, 255));
        // Rough strip
        Raylib.DrawRectangleLinesEx(canvas, 18, new Color(40, 96, 32, 255));

        var hole = _course[_holeIdx];

        // Hazards
        foreach (var (c, rx, ry, kind) in hole.Hazards)
        {
            int cx = (int)(canvasOrigin.X + c.X);
            int cy = (int)(canvasOrigin.Y + c.Y);
            var col = kind == 0 ? new Color(232, 208, 144, 255) : new Color(48, 96, 192, 255);
            Raylib.DrawEllipse(cx, cy, (int)rx, (int)ry, col);
            if (kind == 1)
                Raylib.DrawEllipseLines(cx, cy, (int)rx, (int)ry, new Color(80, 144, 232, 255));
        }

        // Green
        var cup = hole.Cup;
        Raylib.DrawCircle((int)(canvasOrigin.X + cup.X), (int)(canvasOrigin.Y + cup.Y),
            36, new Color(96, 184, 80, 255));

        // Trail
        for (int i = 0; i < _trail.Count; i++)
        {
            byte a = (byte)(180 * (i + 1) / _trail.Count);
            Raylib.DrawCircle(
                (int)(canvasOrigin.X + _trail[i].X),
                (int)(canvasOrigin.Y + _trail[i].Y),
                2, new Color(255, 255, 255, a));
        }

        // Trees
        foreach (var t in hole.Trees)
        {
            int cx = (int)(canvasOrigin.X + t.X);
            int cy = (int)(canvasOrigin.Y + t.Y);
            Raylib.DrawCircle(cx + 1, cy + 2, 12, new Color(0, 0, 0, 60));
            Raylib.DrawCircle(cx, cy, 12, new Color(40, 120, 60, 255));
            Raylib.DrawCircle(cx - 3, cy - 3, 4, new Color(80, 168, 96, 255));
            Raylib.DrawRectangle(cx - 2, cy + 8, 4, 6, new Color(80, 56, 32, 255));
        }

        // Cup + flag
        Raylib.DrawCircle((int)(canvasOrigin.X + cup.X), (int)(canvasOrigin.Y + cup.Y), 6,
            new Color(0, 0, 0, 255));
        int fx = (int)(canvasOrigin.X + cup.X);
        int fy = (int)(canvasOrigin.Y + cup.Y);
        Raylib.DrawRectangle(fx - 1, fy - 26, 2, 26, RetroSkin.BodyText);
        Raylib.DrawTriangle(
            new Vector2(fx + 1, fy - 26),
            new Vector2(fx + 14, fy - 22),
            new Vector2(fx + 1, fy - 18),
            new Color(220, 60, 60, 255));

        // Tee marker
        var tee = hole.Tee;
        Raylib.DrawCircleLines((int)(canvasOrigin.X + tee.X), (int)(canvasOrigin.Y + tee.Y),
            7, new Color(255, 255, 255, 220));

        // Aim line + power bar
        if (_aiming)
        {
            var ballAbs = canvasOrigin + _ball;
            var dir = _ball - _aimEnd;
            float pwr = MathF.Min(dir.Length() * 4f, 380f);
            float pwrFrac = pwr / 380f;
            if (dir.LengthSquared() > 0.1f)
            {
                var endAbs = canvasOrigin + _ball + Vector2.Normalize(dir) * MathF.Min(dir.Length(), 110f);
                Raylib.DrawLineEx(ballAbs, endAbs, 2f, new Color(255, 255, 255, 220));
                // Arrowhead
                var n = Vector2.Normalize(dir);
                var perp = new Vector2(-n.Y, n.X);
                Raylib.DrawTriangle(
                    endAbs,
                    endAbs - n * 8 + perp * 4,
                    endAbs - n * 8 - perp * 4,
                    new Color(255, 255, 255, 220));
            }
            // Power bar at top of canvas
            int barX = (int)canvas.X + 12;
            int barY = (int)canvas.Y + 12;
            int barW = 120;
            Raylib.DrawRectangle(barX - 1, barY - 1, barW + 2, 12, RetroSkin.BodyText);
            Raylib.DrawRectangle(barX, barY, barW, 10, new Color(40, 40, 40, 200));
            var fillCol = pwrFrac < 0.5f ? new Color(60, 200, 60, 255)
                        : pwrFrac < 0.85f ? new Color(232, 200, 64, 255)
                                          : new Color(220, 60, 60, 255);
            Raylib.DrawRectangle(barX, barY, (int)(barW * pwrFrac), 10, fillCol);
            RetroSkin.DrawText($"{(int)pwr}", barX + barW + 6, barY - 2,
                new Color(255, 255, 255, 220), 14);
        }

        // Ball
        Raylib.DrawCircle((int)(canvasOrigin.X + _ball.X) + 1, (int)(canvasOrigin.Y + _ball.Y) + 1,
            5, new Color(0, 0, 0, 100));
        Raylib.DrawCircle((int)(canvasOrigin.X + _ball.X), (int)(canvasOrigin.Y + _ball.Y),
            5, new Color(255, 255, 255, 255));
        Raylib.DrawCircleLines((int)(canvasOrigin.X + _ball.X), (int)(canvasOrigin.Y + _ball.Y),
            5, RetroSkin.BodyText);

        // Hole-completed flash
        if (_holeComplete)
        {
            string msg = $"Holed in {_strokes[_holeIdx]} (par {_course[_holeIdx].Par})";
            int w = RetroSkin.MeasureText(msg, 22);
            int x = (int)(canvas.X + (canvas.Width - w) / 2);
            int y = (int)(canvas.Y + canvas.Height / 2 - 12);
            Raylib.DrawRectangle(x - 12, y - 6, w + 24, 36, new Color(0, 0, 0, 180));
            RetroSkin.DrawText(msg, x, y, new Color(255, 240, 140, 255), 22);
        }

        // Round-completed banner
        if (_roundComplete)
        {
            int total = 0, parTotal = 0;
            for (int i = 0; i < Holes; i++) { total += _strokes[i]; parTotal += _course[i].Par; }
            int rel = total - parTotal;
            string headline = "Round complete";
            string score = rel == 0 ? $"{total} — even par"
                         : rel < 0 ? $"{total} — {rel} under par!"
                                   : $"{total} — +{rel} over par";
            int wHead = RetroSkin.MeasureText(headline, 24);
            int wScore = RetroSkin.MeasureText(score, 18);
            int x = (int)canvas.X + (CanvasW - Math.Max(wHead, wScore)) / 2;
            int y = (int)canvas.Y + CanvasH / 2 - 30;
            Raylib.DrawRectangle(x - 16, y - 12, Math.Max(wHead, wScore) + 32, 70,
                new Color(0, 0, 0, 200));
            RetroSkin.DrawText(headline,
                (int)canvas.X + (CanvasW - wHead) / 2, y, new Color(255, 240, 140, 255), 24);
            RetroSkin.DrawText(score,
                (int)canvas.X + (CanvasW - wScore) / 2, y + 28, new Color(255, 255, 255, 255), 18);
        }
    }

    private void DrawScorecard(Vector2 panelOffset)
    {
        float sx = panelOffset.X + FrameInset + CanvasW + Margin;
        float sy = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight;
        var rect = new Rectangle(sx, sy, ScoreW - Margin, CanvasH);
        RetroSkin.DrawSunken(rect, RetroSkin.Face);

        RetroSkin.DrawText("Scorecard", (int)sx + 8, (int)sy + 6, RetroSkin.BodyText, 16);
        // Header row
        int hx = (int)sx + 8;
        int hy = (int)sy + 28;
        RetroSkin.DrawText("Hole", hx, hy, RetroSkin.BodyText, 13);
        RetroSkin.DrawText("Par", hx + 50, hy, RetroSkin.BodyText, 13);
        RetroSkin.DrawText("You", hx + 90, hy, RetroSkin.BodyText, 13);
        Raylib.DrawLine(hx, hy + 16, hx + (int)rect.Width - 16, hy + 16, RetroSkin.Shadow);

        int rowY = hy + 22;
        int totalPar = 0, totalStrokes = 0;
        for (int i = 0; i < Holes; i++)
        {
            bool current = i == _holeIdx && !_roundComplete;
            var col = current ? new Color(40, 80, 168, 255) : RetroSkin.BodyText;
            RetroSkin.DrawText((i + 1).ToString(), hx, rowY, col, 13);
            RetroSkin.DrawText(_course[i].Par.ToString(), hx + 50, rowY, col, 13);
            string strokes = (i <= _holeIdx || _roundComplete) ? _strokes[i].ToString() : "—";
            RetroSkin.DrawText(strokes, hx + 90, rowY, col, 13);
            totalPar += _course[i].Par;
            totalStrokes += _strokes[i];
            rowY += 16;
        }
        Raylib.DrawLine(hx, rowY, hx + (int)rect.Width - 16, rowY, RetroSkin.Shadow);
        rowY += 4;
        RetroSkin.DrawText("Total", hx, rowY, RetroSkin.BodyText, 13);
        RetroSkin.DrawText(totalPar.ToString(), hx + 50, rowY, RetroSkin.BodyText, 13);
        RetroSkin.DrawText(totalStrokes.ToString(), hx + 90, rowY, RetroSkin.BodyText, 13);
    }

    public void Close() { }
}
