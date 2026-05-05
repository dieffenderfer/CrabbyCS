using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Fuji Golf — top-down hole. Drag from the ball to aim and set power; longer
/// drag = bigger swing. The ball decelerates with friction that depends on
/// terrain (fairway / rough / sand). Hitting the cup wins. One hole per round.
/// </summary>
public class FujiGolfActivity : IActivity
{
    private const int FrameInset = 3;
    private const int CanvasW = 480;
    private const int CanvasH = 320;

    public Vector2 PanelSize => new(
        2 * FrameInset + CanvasW,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + CanvasH + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private Vector2 _ball;
    private Vector2 _vel;
    private bool _aiming;
    private Vector2 _aimEnd;
    private int _strokes;
    private bool _won;
    private int _par = 4;
    private Vector2 _hole = new(420, 60);

    // Terrain: each pixel of canvas categorized by simple shape masks.
    // 0 fairway, 1 rough, 2 sand, 3 water, 4 green (slick)
    private int Terrain(Vector2 p)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= CanvasW || p.Y >= CanvasH) return 1; // OOB rough
        // Water hazard: a blob around (260, 220)
        if ((p - new Vector2(260, 220)).LengthSquared() < 60 * 60) return 3;
        // Sand bunker: oval near (380, 130)
        var d = p - new Vector2(380, 130);
        if (d.X * d.X * 0.6f + d.Y * d.Y * 1.4f < 30 * 30) return 2;
        // Green around hole
        if ((p - _hole).LengthSquared() < 50 * 50) return 4;
        // Rough strip near edges
        if (p.X < 24 || p.Y < 24 || p.X > CanvasW - 24 || p.Y > CanvasH - 24) return 1;
        return 0;
    }

    public void Load() => Reset();

    private void Reset()
    {
        _ball = new Vector2(60, 260);
        _vel = Vector2.Zero;
        _aiming = false;
        _strokes = 0;
        _won = false;
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
        { Reset(); return; }

        var canvasOrigin = new Vector2(FrameInset, FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight);
        var canvasMouse = local - canvasOrigin;

        // Update ball motion
        if (_vel.LengthSquared() > 0.01f)
        {
            _ball += _vel * delta;
            int t = Terrain(_ball);
            float fric = t switch { 2 => 6.0f, 1 => 3.0f, 4 => 1.0f, _ => 1.6f };
            _vel *= MathF.Max(0, 1f - fric * delta);
            // Walls bounce
            if (_ball.X < 4 || _ball.X > CanvasW - 4) { _vel.X = -_vel.X * 0.6f; _ball.X = Math.Clamp(_ball.X, 4, CanvasW - 4); }
            if (_ball.Y < 4 || _ball.Y > CanvasH - 4) { _vel.Y = -_vel.Y * 0.6f; _ball.Y = Math.Clamp(_ball.Y, 4, CanvasH - 4); }
            // Water: penalty drop
            if (Terrain(_ball) == 3)
            {
                _ball = new Vector2(60, 260);
                _vel = Vector2.Zero;
                _strokes++;
            }
            // Hole
            if ((_ball - _hole).LengthSquared() < 9 * 9 && _vel.Length() < 80f)
            { _won = true; _vel = Vector2.Zero; }
            return;
        }

        if (_won) return;

        // Aiming
        if (leftPressed && (canvasMouse - _ball).LengthSquared() < 12 * 12)
            _aiming = true;
        if (_aiming) _aimEnd = canvasMouse;
        if (leftReleased && _aiming)
        {
            _aiming = false;
            var dir = _ball - _aimEnd;
            float power = Math.Min(dir.Length() * 4f, 360f);
            if (power < 8) return;
            _vel = Vector2.Normalize(dir) * power;
            _strokes++;
        }
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Fuji Golf", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New" }, -1);

        var canvasOrigin = new Vector2(
            panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight);
        var canvas = new Rectangle(canvasOrigin.X, canvasOrigin.Y, CanvasW, CanvasH);

        // Fairway base
        Raylib.DrawRectangleRec(canvas, new Color(64, 144, 64, 255));
        // Rough strip
        Raylib.DrawRectangleLinesEx(canvas, 24, new Color(40, 96, 32, 255));
        // Water
        Raylib.DrawCircle((int)(canvasOrigin.X + 260), (int)(canvasOrigin.Y + 220), 60, new Color(48, 96, 192, 255));
        // Sand
        Raylib.DrawEllipse((int)(canvasOrigin.X + 380), (int)(canvasOrigin.Y + 130), 32, 22, new Color(232, 208, 144, 255));
        // Green
        Raylib.DrawCircle((int)(canvasOrigin.X + _hole.X), (int)(canvasOrigin.Y + _hole.Y), 50, new Color(96, 184, 80, 255));
        // Hole
        Raylib.DrawCircle((int)(canvasOrigin.X + _hole.X), (int)(canvasOrigin.Y + _hole.Y), 6, new Color(0, 0, 0, 255));
        // Flag
        Raylib.DrawRectangle((int)(canvasOrigin.X + _hole.X) - 1, (int)(canvasOrigin.Y + _hole.Y) - 24, 2, 24, RetroSkin.BodyText);
        Raylib.DrawTriangle(
            new Vector2(canvasOrigin.X + _hole.X + 1, canvasOrigin.Y + _hole.Y - 24),
            new Vector2(canvasOrigin.X + _hole.X + 14, canvasOrigin.Y + _hole.Y - 20),
            new Vector2(canvasOrigin.X + _hole.X + 1, canvasOrigin.Y + _hole.Y - 16),
            new Color(220, 60, 60, 255));

        // Aim line
        if (_aiming)
        {
            var ballAbs = canvasOrigin + _ball;
            var endAbs = canvasOrigin + _aimEnd;
            var dir = _ball - _aimEnd;
            var arrowEnd = canvasOrigin + _ball + Vector2.Normalize(dir) * MathF.Min(dir.Length(), 90f);
            Raylib.DrawLineEx(ballAbs, arrowEnd, 2f, new Color(255, 255, 255, 220));
        }

        // Ball
        Raylib.DrawCircle((int)(canvasOrigin.X + _ball.X), (int)(canvasOrigin.Y + _ball.Y), 4, new Color(255, 255, 255, 255));
        Raylib.DrawCircleLines((int)(canvasOrigin.X + _ball.X), (int)(canvasOrigin.Y + _ball.Y), 4, RetroSkin.BodyText);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _won ? $"Holed in {_strokes}!" : "Drag from ball to aim";
        RetroWidgets.StatusBar(status, state, $"Strokes: {_strokes}   Par: {_par}");
    }

    public void Close() { }
}
