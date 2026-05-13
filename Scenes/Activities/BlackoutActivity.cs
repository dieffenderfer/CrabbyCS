using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Blackout — port of FUNGAMES/BLACKOUT (Patrick Mills / Zarkware, 1991+).
/// Mechanics drawn from BLACKOUT.WRI:
///
///   • A grid of coloured rectangles. Some are TARGETS (drawn with a
///     bullseye). A few are BONUS tiles labelled 2x / 3x / 4x / 5x.
///   • Click a target → it turns red, scores points, plays a "found" cue.
///   • Click a bonus → it turns blue and multiplies the round score.
///   • Click a non-target → that cell PLUS the eight neighbours all go
///     black. (The doc calls this the "missed" penalty.)
///   • If the non-targets all black out before you finish the targets,
///     a "blackout" occurs and the game ends. Win by clearing every
///     target before that happens.
///
/// Tuned for a quick round (~30s per board) so you can do "one more"
/// without committing to a long session.
/// </summary>
public class BlackoutActivity : IActivity
{
    public Vector2 PanelSize => new(560, 460);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int Cols = 10;
    private const int Rows = 8;

    private enum Cell { Empty, Target, Bonus2, Bonus3, Bonus5, Found, Hit, Black }

    private Cell[,] _grid = new Cell[Cols, Rows];
    private int _score;
    private int _multiplier = 1;
    private int _targetsRemaining;
    private int _nonTargetsRemaining;
    private bool _gameOver;
    private bool _won;
    private float _decayTimer;          // delay between automatic black-outs
    private string _status = "Click the bullseye targets — avoid the blanks!";
    private readonly Random _rng = new();

    public void Load() => NewGame();
    public void Close() => IsFinished = true;

    private void NewGame()
    {
        Array.Clear(_grid);
        _score = 0;
        _multiplier = 1;
        _gameOver = false;
        _won = false;
        _decayTimer = 0;

        // Place targets and bonuses, randomly seeded but balanced so you
        // can finish in a few clicks if accurate. Counts based on the
        // grid size — ~15% targets, ~5% bonuses, rest empty.
        int total = Cols * Rows;
        int wantTargets = (int)(total * 0.18);
        int wantBonus = (int)(total * 0.06);
        var coords = new List<(int x, int y)>();
        for (int x = 0; x < Cols; x++) for (int y = 0; y < Rows; y++) coords.Add((x, y));
        Shuffle(coords);
        int i = 0;
        for (int t = 0; t < wantTargets && i < coords.Count; t++, i++)
            _grid[coords[i].x, coords[i].y] = Cell.Target;
        for (int b = 0; b < wantBonus && i < coords.Count; b++, i++)
        {
            // Distribute roughly evenly across the four bonus types.
            _grid[coords[i].x, coords[i].y] = (b % 3) switch
            {
                0 => Cell.Bonus2,
                1 => Cell.Bonus3,
                _ => Cell.Bonus5,
            };
        }

        _targetsRemaining = wantTargets;
        // Non-target cells = grid minus targets and bonuses. Bonuses don't
        // count toward the blackout loss condition (they remain optional).
        _nonTargetsRemaining = total - wantTargets - wantBonus;
        _status = $"{wantTargets} targets, {wantBonus} bonuses. Click the bullseyes!";
    }

    private void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
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
            case 0: NewGame(); return;
            case 1: _status = "Targets +10. Bonuses multiply x2/x3/x5. Misses black out neighbours."; return;
        }

        if (_gameOver) return;

        // Slow drip black-out: one cell darkens every couple of seconds so
        // the player can't sit still. Matches the original's time pressure
        // (configurable in the .HLP we can't read; tuned by feel here).
        _decayTimer += delta;
        if (_decayTimer >= 2.0f)
        {
            _decayTimer = 0;
            BlackOneEmpty();
        }

        var grid = GridRect();
        if (leftPressed && RetroSkin.PointInRect(local, grid))
        {
            float cellW = grid.Width / Cols;
            float cellH = grid.Height / Rows;
            int cx = Math.Clamp((int)((local.X - grid.X) / cellW), 0, Cols - 1);
            int cy = Math.Clamp((int)((local.Y - grid.Y) / cellH), 0, Rows - 1);
            ClickCell(cx, cy);
        }
    }

    private void BlackOneEmpty()
    {
        // Pick a random un-clicked Empty cell and black it out.
        var picks = new List<(int x, int y)>();
        for (int x = 0; x < Cols; x++)
            for (int y = 0; y < Rows; y++)
                if (_grid[x, y] == Cell.Empty) picks.Add((x, y));
        if (picks.Count == 0) return;
        var (px, py) = picks[_rng.Next(picks.Count)];
        _grid[px, py] = Cell.Black;
        _nonTargetsRemaining--;
        CheckEnd();
    }

    private void ClickCell(int x, int y)
    {
        var c = _grid[x, y];
        switch (c)
        {
            case Cell.Target:
                _grid[x, y] = Cell.Found;
                _score += 10 * _multiplier;
                _targetsRemaining--;
                _status = $"+{10 * _multiplier}!  {_targetsRemaining} target{(_targetsRemaining == 1 ? "" : "s")} left.";
                CheckEnd();
                break;
            case Cell.Bonus2:
            case Cell.Bonus3:
            case Cell.Bonus5:
                _multiplier *= c == Cell.Bonus2 ? 2 : c == Cell.Bonus3 ? 3 : 5;
                _grid[x, y] = Cell.Found;
                _status = $"Bonus! Multiplier now ×{_multiplier}.";
                break;
            case Cell.Empty:
                // Miss penalty — blackout 3x3 area centered on click.
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= Cols || ny >= Rows) continue;
                        if (_grid[nx, ny] == Cell.Empty)
                        {
                            _grid[nx, ny] = Cell.Black;
                            _nonTargetsRemaining--;
                        }
                    }
                }
                _grid[x, y] = Cell.Hit;
                _score = Math.Max(0, _score - 5);
                _status = "Miss! −5, and the neighbours went dark.";
                CheckEnd();
                break;
        }
    }

    private void CheckEnd()
    {
        if (_targetsRemaining <= 0)
        {
            _won = true;
            _gameOver = true;
            _status = $"All targets cleared! Final score {_score}.";
            return;
        }
        if (_nonTargetsRemaining <= 0)
        {
            _gameOver = true;
            _status = $"Blackout! Final score {_score}.";
        }
    }

    private Rectangle GridRect()
    {
        float top = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 12;
        float bottom = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 12;
        float side = Math.Min(PanelSize.X - 32, bottom - top);
        // Maintain a 10:8 aspect ratio with a touch of horizontal padding.
        float gridH = side;
        float gridW = gridH * Cols / Rows;
        if (gridW > PanelSize.X - 32) { gridW = PanelSize.X - 32; gridH = gridW * Rows / Cols; }
        return new Rectangle((PanelSize.X - gridW) / 2f, top + (bottom - top - gridH) / 2f, gridW, gridH);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Blackout", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Help" }, -1);

        var grid = GridRect();
        var gridAbs = new Rectangle(panelOffset.X + grid.X, panelOffset.Y + grid.Y, grid.Width, grid.Height);
        Raylib.DrawRectangleRec(gridAbs, RetroSkin.Face);

        float cellW = grid.Width / Cols;
        float cellH = grid.Height / Rows;
        for (int x = 0; x < Cols; x++)
        {
            for (int y = 0; y < Rows; y++)
            {
                var r = new Rectangle(gridAbs.X + x * cellW + 2, gridAbs.Y + y * cellH + 2,
                    cellW - 4, cellH - 4);
                DrawCell(_grid[x, y], r);
            }
        }

        if (_gameOver)
        {
            var bannerR = new Rectangle(
                panelOffset.X + PanelSize.X / 2 - 200,
                panelOffset.Y + PanelSize.Y / 2 - 50,
                400, 100);
            RetroSkin.DrawRaised(bannerR);
            string msg = _won ? $"Cleared! Score {_score}" : $"Blackout! Score {_score}";
            int mw = FontManager.MeasureText(msg, 22);
            FontManager.DrawText(msg,
                (int)(bannerR.X + (bannerR.Width - mw) / 2),
                (int)(bannerR.Y + 28),
                22, RetroSkin.BodyText);
            const string hint = "Press New for another round.";
            int hw = FontManager.MeasureText(hint, 14);
            FontManager.DrawText(hint,
                (int)(bannerR.X + (bannerR.Width - hw) / 2),
                (int)(bannerR.Y + 64),
                14, RetroSkin.DisabledText);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _status,
            $"Score {_score}  ×{_multiplier}  Targets {_targetsRemaining}");
    }

    private static void DrawCell(Cell c, Rectangle r)
    {
        switch (c)
        {
            case Cell.Empty:
                Raylib.DrawRectangleRec(r, new Color((byte)252, (byte)244, (byte)216, (byte)255));
                Raylib.DrawRectangleLinesEx(r, 1, RetroSkin.Shadow);
                break;
            case Cell.Target:
                Raylib.DrawRectangleRec(r, new Color((byte)252, (byte)252, (byte)252, (byte)255));
                Raylib.DrawRectangleLinesEx(r, 1, RetroSkin.Shadow);
                // Concentric rings = bullseye.
                float cx = r.X + r.Width / 2;
                float cy = r.Y + r.Height / 2;
                float rad = Math.Min(r.Width, r.Height) * 0.4f;
                Raylib.DrawCircle((int)cx, (int)cy, rad, new Color((byte)200, (byte)60, (byte)64, (byte)255));
                Raylib.DrawCircle((int)cx, (int)cy, rad * 0.66f, new Color((byte)252, (byte)244, (byte)216, (byte)255));
                Raylib.DrawCircle((int)cx, (int)cy, rad * 0.33f, new Color((byte)200, (byte)60, (byte)64, (byte)255));
                break;
            case Cell.Bonus2:
            case Cell.Bonus3:
            case Cell.Bonus5:
                Raylib.DrawRectangleRec(r, new Color((byte)252, (byte)232, (byte)96, (byte)255));
                Raylib.DrawRectangleLinesEx(r, 1, RetroSkin.DarkShadow);
                string label = c == Cell.Bonus2 ? "2x" : c == Cell.Bonus3 ? "3x" : "5x";
                int w = FontManager.MeasureText(label, 18);
                FontManager.DrawText(label,
                    (int)(r.X + (r.Width - w) / 2),
                    (int)(r.Y + (r.Height - 18) / 2),
                    18, RetroSkin.BodyText);
                break;
            case Cell.Found:
                Raylib.DrawRectangleRec(r, new Color((byte)108, (byte)196, (byte)116, (byte)255));
                Raylib.DrawRectangleLinesEx(r, 1, RetroSkin.DarkShadow);
                break;
            case Cell.Hit:
                Raylib.DrawRectangleRec(r, new Color((byte)200, (byte)80, (byte)80, (byte)255));
                Raylib.DrawRectangleLinesEx(r, 1, RetroSkin.DarkShadow);
                break;
            case Cell.Black:
                Raylib.DrawRectangleRec(r, new Color((byte)20, (byte)20, (byte)28, (byte)255));
                break;
        }
    }
}
