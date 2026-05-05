using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// SkiFree — scroll downhill on a procedurally-spawned slope. Steer left/right
/// to dodge trees and rocks. After a while a hairy biped joins in and chases.
/// Hitting a tree or getting caught ends the run.
/// </summary>
public class SkiFreeActivity : IActivity
{
    private const int FrameInset = 3;
    private const int CanvasW = 400;
    private const int CanvasH = 300;

    public Vector2 PanelSize => new(
        2 * FrameInset + CanvasW,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + CanvasH + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly RetroHelp _help = new()
    {
        Title = "SkiFree — How to play",
        Lines = new[]
        {
            "Steer down the slope with the left/right arrow keys.",
            "Down arrow tucks for extra speed.",
            "Dodge trees and rocks; pass slalom flags for bonus points.",
            "After 800m a hairy biped joins the chase — outrun it.",
            "Hitting any obstacle ends the run.",
        },
    };

    private record struct Obstacle(float X, float Y, int Kind); // 0 tree, 1 rock, 2 flag
    private List<Obstacle> _obs = new();
    private float _skierX;
    private float _skierVx;       // -1, 0, +1 horizontal lean
    private float _scrollSpeed = 60f;
    private float _scrollY;
    private int _distance;
    private bool _gameOver;
    private float _spawnTimer;
    private bool _yetiActive;
    private float _yetiX, _yetiY;
    private float _yetiTimer;
    private readonly Random _rng = new();

    public void Load() => Reset();

    private void Reset()
    {
        _obs.Clear();
        _skierX = CanvasW / 2f;
        _skierVx = 0;
        _scrollY = 0;
        _distance = 0;
        _gameOver = false;
        _spawnTimer = 0;
        _yetiActive = false;
        _yetiTimer = 0;
        _scrollSpeed = 60f;
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
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Help" }, local, leftPressed))
        {
            case 0: Reset(); return;
            case 1: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (_gameOver) return;

        // Steering
        _skierVx = 0;
        if (Raylib.IsKeyDown(KeyboardKey.Left)) _skierVx = -1;
        if (Raylib.IsKeyDown(KeyboardKey.Right)) _skierVx = 1;
        if (Raylib.IsKeyDown(KeyboardKey.Down)) _scrollSpeed = 140f;
        else _scrollSpeed = Math.Min(_scrollSpeed + delta * 4f, 80f + _distance * 0.05f);

        _skierX += _skierVx * 120f * delta;
        _skierX = Math.Clamp(_skierX, 12, CanvasW - 12);

        _scrollY += _scrollSpeed * delta;
        _distance = (int)(_scrollY / 4);

        // Spawn obstacles
        _spawnTimer += delta;
        if (_spawnTimer > 0.18f)
        {
            _spawnTimer = 0;
            int kind = _rng.Next(10) switch { < 6 => 0, < 9 => 1, _ => 2 };
            _obs.Add(new Obstacle(_rng.Next(8, CanvasW - 8), CanvasH + 10, kind));
        }

        // Move obstacles up (relative to skier)
        for (int i = _obs.Count - 1; i >= 0; i--)
        {
            var o = _obs[i];
            o.Y -= _scrollSpeed * delta;
            _obs[i] = o;
            if (o.Y < -20) _obs.RemoveAt(i);
        }

        // Skier collision (skier sits at fixed canvas y = 80)
        const float skierY = 80f;
        foreach (var o in _obs)
        {
            float dx = o.X - _skierX, dy = o.Y - skierY;
            if (dx * dx + dy * dy < (o.Kind == 1 ? 9 * 9 : 12 * 12))
            {
                if (o.Kind == 2) { _distance += 50; }
                else { _gameOver = true; return; }
            }
        }

        // Yeti spawns after a while
        if (!_yetiActive && _distance > 800)
        {
            _yetiActive = true;
            _yetiX = _rng.Next(0, 2) == 0 ? -10 : CanvasW + 10;
            _yetiY = CanvasH;
        }
        if (_yetiActive)
        {
            _yetiTimer += delta;
            float dx = Math.Sign(_skierX - _yetiX);
            float dy = Math.Sign(skierY - _yetiY);
            _yetiX += dx * 50f * delta;
            _yetiY += dy * 60f * delta;
            float ddx = _yetiX - _skierX, ddy = _yetiY - skierY;
            if (ddx * ddx + ddy * ddy < 14 * 14) { _gameOver = true; return; }
        }
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "SkiFree", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Help" }, -1);

        var canvas = new Rectangle(
            panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight,
            CanvasW, CanvasH);
        Raylib.DrawRectangleRec(canvas, new Color(232, 240, 252, 255));

        Raylib.BeginScissorMode((int)canvas.X, (int)canvas.Y, (int)canvas.Width, (int)canvas.Height);
        // Snow specks scrolling
        for (int i = 0; i < 60; i++)
        {
            int sy = ((int)(_scrollY * 0.6f) + i * 23) % CanvasH;
            int sx = (i * 71) % CanvasW;
            Raylib.DrawPixel((int)canvas.X + sx, (int)canvas.Y + (CanvasH - sy), new Color(200, 220, 240, 255));
        }

        // Obstacles
        foreach (var o in _obs)
        {
            int ox = (int)(canvas.X + o.X);
            int oy = (int)(canvas.Y + o.Y);
            if (o.Kind == 0)
            {
                // Tree: triangle + trunk
                Raylib.DrawTriangle(
                    new Vector2(ox - 10, oy + 10),
                    new Vector2(ox, oy - 12),
                    new Vector2(ox + 10, oy + 10),
                    new Color(40, 120, 60, 255));
                Raylib.DrawRectangle(ox - 2, oy + 8, 4, 6, new Color(80, 56, 32, 255));
            }
            else if (o.Kind == 1)
            {
                Raylib.DrawCircle(ox, oy, 7, new Color(120, 120, 120, 255));
                Raylib.DrawCircle(ox - 2, oy - 2, 2, new Color(180, 180, 180, 255));
            }
            else
            {
                // Flag (slalom gate, scored)
                Raylib.DrawRectangle(ox - 1, oy - 12, 2, 14, new Color(40, 40, 40, 255));
                Raylib.DrawTriangle(
                    new Vector2(ox + 1, oy - 12),
                    new Vector2(ox + 12, oy - 8),
                    new Vector2(ox + 1, oy - 4),
                    new Color(220, 60, 60, 255));
            }
        }

        // Skier (fixed y = 80)
        int kx = (int)(canvas.X + _skierX);
        int ky = (int)(canvas.Y + 80);
        Raylib.DrawCircle(kx, ky - 8, 4, new Color(220, 120, 80, 255)); // head
        Raylib.DrawRectangle(kx - 3, ky - 5, 6, 8, new Color(40, 60, 140, 255));
        // Skis
        Raylib.DrawLine(kx - 6 + (int)(_skierVx * 3), ky + 6, kx - 4 + (int)(_skierVx * 3), ky + 12, RetroSkin.BodyText);
        Raylib.DrawLine(kx + 6 + (int)(_skierVx * 3), ky + 6, kx + 4 + (int)(_skierVx * 3), ky + 12, RetroSkin.BodyText);

        // Yeti
        if (_yetiActive)
        {
            int yx = (int)(canvas.X + _yetiX), yy = (int)(canvas.Y + _yetiY);
            Raylib.DrawCircle(yx, yy - 6, 6, new Color(230, 230, 240, 255));
            Raylib.DrawRectangle(yx - 5, yy - 2, 10, 10, new Color(230, 230, 240, 255));
            Raylib.DrawCircle(yx - 2, yy - 6, 1, RetroSkin.BodyText);
            Raylib.DrawCircle(yx + 2, yy - 6, 1, RetroSkin.BodyText);
        }
        Raylib.EndScissorMode();

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string state = _gameOver ? "Crashed!" : "← → steer  ↓ tuck";
        RetroWidgets.StatusBar(status, state, $"Distance: {_distance}m");

        _help.Draw(panelOffset, PanelSize);
    }

    public void Close() { }
}
