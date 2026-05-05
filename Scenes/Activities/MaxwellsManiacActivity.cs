using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Maxwell's Maniac — color-pattern memory. Each round the panel flashes a
/// growing sequence of colored quadrants; repeat the sequence by clicking the
/// quadrants in order. Sequence length doubles as score.
/// </summary>
public class MaxwellsManiacActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Margin = 24;
    private const int Pad = 16;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + 2 * 130 + Pad,
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + 2 * 130 + Pad + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly RetroHelp _help = new()
    {
        Title = "Maxwell's Maniac — How to play",
        Lines = new[]
        {
            "Watch the panel flash a sequence of colored quadrants.",
            "Repeat the sequence by clicking the same quadrants in order.",
            "Each round adds one more flash to the sequence.",
            "One wrong click ends the round.",
            "(Beta: stand-in for the original Maniac mechanics.)",
        },
    };

    private static readonly Color[] PadColors =
    {
        new(60, 200, 60, 255),
        new(220, 60, 60, 255),
        new(60, 100, 220, 255),
        new(232, 200, 60, 255),
    };
    private static readonly Color[] PadDim =
    {
        new(20, 100, 20, 255),
        new(120, 30, 30, 255),
        new(30, 50, 120, 255),
        new(140, 120, 30, 255),
    };

    private List<int> _seq = new();
    private int _playIdx = 0;
    private bool _showing;
    private float _showTimer;
    private int _flashIdx = -1;
    private int _score;
    private bool _gameOver;
    private string _msg = "Click Start";
    private readonly Random _rng = new();

    public void Load() { }

    private void StartRound()
    {
        _seq.Clear();
        _score = 0;
        _gameOver = false;
        AddNote();
    }

    private void AddNote()
    {
        _seq.Add(_rng.Next(4));
        _showing = true;
        _showTimer = 0;
        _playIdx = 0;
        _flashIdx = -1;
        _msg = $"Watch  ({_seq.Count})";
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
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "Start", "Help" }, local, leftPressed))
        {
            case 0: StartRound(); return;
            case 1: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (_showing)
        {
            _showTimer += delta;
            int step = (int)(_showTimer / 0.5f);
            if (step >= _seq.Count) { _showing = false; _flashIdx = -1; _msg = "Your turn"; return; }
            _flashIdx = (_showTimer % 0.5f < 0.3f) ? _seq[step] : -1;
            return;
        }

        if (_gameOver) return;

        if (leftPressed)
        {
            for (int i = 0; i < 4; i++)
            {
                if (RetroSkin.PointInRect(local, PadRect(i)))
                {
                    _flashIdx = i;
                    if (_seq[_playIdx] == i)
                    {
                        _playIdx++;
                        if (_playIdx >= _seq.Count)
                        {
                            _score = _seq.Count;
                            AddNote();
                        }
                    }
                    else
                    {
                        _gameOver = true;
                        _msg = $"Game over — score {_score}";
                    }
                    return;
                }
            }
        }
    }

    private Rectangle PadRect(int i)
    {
        float bx = FrameInset + Margin;
        float by = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        int x = i % 2, y = i / 2;
        return new Rectangle(bx + x * (130 + Pad), by + y * (130 + Pad), 130, 130);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Maxwell's Maniac", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "Start", "Help" }, -1);

        for (int i = 0; i < 4; i++)
        {
            var r = PadRect(i);
            var abs = new Rectangle(panelOffset.X + r.X, panelOffset.Y + r.Y, r.Width, r.Height);
            var col = _flashIdx == i ? PadColors[i] : PadDim[i];
            Raylib.DrawRectangleRec(abs, col);
            RetroSkin.DrawRaised(abs, fill: false);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _msg, $"Score: {_score}");

        _help.Draw(panelOffset, PanelSize);
    }

    public void Close() { }
}
