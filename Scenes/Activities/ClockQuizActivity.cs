using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Clock Quiz — show an analog clock face, the user types the digital
/// time as "HH:MM". Three difficulty bands change the minute granularity
/// (5-min steps → 1-min steps) so a beginner can practice the o'clock /
/// half-past read before tackling 13-past-2.
///
/// Inspired by the CLOCKS category of the "201 Learning Games" collection.
/// </summary>
public class ClockQuizActivity : IActivity
{
    public Vector2 PanelSize => new(420, 460);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;

    private enum Diff { Easy, Medium, Hard }
    private Diff _diff = Diff.Easy;
    private int _hour;       // 1..12
    private int _minute;     // 0..59
    private string _input = "";
    private int _correctRun;
    private int _wrongRun;
    private string _feedback = "Read the clock and type the time as HH:MM.";
    private bool _showAnswer;
    private float _showAnswerTimer;
    private readonly Random _rng = new();

    public void Load() => NewQuestion();
    public void Close() => IsFinished = true;

    private void NewQuestion()
    {
        _hour = _rng.Next(1, 13);
        _minute = _diff switch
        {
            Diff.Easy => _rng.Next(0, 12) * 5,       // 5-min steps
            Diff.Medium => _rng.Next(0, 60),         // every minute
            _ => _rng.Next(0, 60),                   // every minute, same as medium
        };
        // Hard difficulty: also use 24-hour clock for input ambiguity check.
        _input = "";
        _showAnswer = false;
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
        var items = new[] {
            _diff == Diff.Easy ? "Easy ✓" : "Easy",
            _diff == Diff.Medium ? "Med ✓" : "Med",
            _diff == Diff.Hard ? "Hard ✓" : "Hard",
            "Skip",
        };
        int hit = RetroWidgets.MenuBarHitTest(menuBar, items, local, leftPressed);
        if (hit == 0) { _diff = Diff.Easy; NewQuestion(); return; }
        if (hit == 1) { _diff = Diff.Medium; NewQuestion(); return; }
        if (hit == 2) { _diff = Diff.Hard; NewQuestion(); return; }
        if (hit == 3) { NewQuestion(); _feedback = "Skipped."; return; }

        if (_showAnswer)
        {
            _showAnswerTimer += delta;
            if (_showAnswerTimer > 2f) { _showAnswerTimer = 0; NewQuestion(); }
            return;
        }

        // "HH:MM" input — colon is forced; user only types digits.
        int ch = Raylib.GetCharPressed();
        while (ch > 0)
        {
            if (ch >= '0' && ch <= '9' && _input.Length < 5)
            {
                _input += (char)ch;
                // Auto-insert colon after two digits so the player doesn't
                // have to fight the keyboard for the colon character.
                if (_input.Length == 2 && !_input.Contains(':')) _input += ":";
            }
            else if (ch == ':' && !_input.Contains(':') && _input.Length > 0)
            {
                _input += ":";
            }
            ch = Raylib.GetCharPressed();
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && _input.Length > 0)
            _input = _input[..^1];
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
            Submit();
    }

    private void Submit()
    {
        var parts = _input.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int h)
            || !int.TryParse(parts[1], out int m))
        {
            _feedback = "Type HH:MM (for example, 3:45)";
            return;
        }
        bool ok = (h == _hour || h == _hour + 12 || h == _hour - 12) && m == _minute;
        if (ok)
        {
            _correctRun++;
            _feedback = "Correct!";
            NewQuestion();
        }
        else
        {
            _correctRun = 0;
            _wrongRun++;
            _feedback = $"That's actually {_hour}:{_minute:D2}";
            _showAnswer = true;
            _showAnswerTimer = 0;
        }
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Clock Quiz", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        var items = new[] {
            _diff == Diff.Easy ? "Easy ✓" : "Easy",
            _diff == Diff.Medium ? "Med ✓" : "Med",
            _diff == Diff.Hard ? "Hard ✓" : "Hard",
            "Skip",
        };
        RetroWidgets.MenuBarVisual(menuBar, items, -1);

        DrawClockFace(panelOffset);

        // Input box
        var box = new Rectangle(
            panelOffset.X + FrameInset + 60,
            panelOffset.Y + PanelSize.Y - 80,
            PanelSize.X - 2 * (FrameInset + 60), 32);
        RetroSkin.DrawSunken(box, RetroSkin.SunkenBg);
        string display = _input.Length == 0 ? "HH:MM" : _input;
        int dw = FontManager.MeasureText(display, 22);
        FontManager.DrawText(display,
            (int)(box.X + (box.Width - dw) / 2),
            (int)(box.Y + 5),
            22, _input.Length == 0 ? RetroSkin.DisabledText : RetroSkin.BodyText);

        // Feedback
        int fw = FontManager.MeasureText(_feedback, 14);
        FontManager.DrawText(_feedback,
            (int)(panelOffset.X + (PanelSize.X - fw) / 2),
            (int)(panelOffset.Y + PanelSize.Y - 44),
            14, _showAnswer ? RetroSkin.TitleActive : RetroSkin.BodyText);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status,
            $"Streak: {_correctRun}   Missed: {_wrongRun}",
            _diff.ToString());
    }

    private void DrawClockFace(Vector2 panelOffset)
    {
        // Clock occupies the top portion of the panel under the menu bar.
        float cx = panelOffset.X + PanelSize.X / 2f;
        float cy = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight
                   + RetroWidgets.MenuBarHeight + 130;
        float radius = 110;

        // Face background — slightly off-white so the dial reads as a
        // dedicated surface rather than blending with the window face.
        Raylib.DrawCircle((int)cx, (int)cy, radius + 6, RetroSkin.Shadow);
        Raylib.DrawCircle((int)cx, (int)cy, radius, new Color(252, 248, 235, 255));
        Raylib.DrawCircleLines((int)cx, (int)cy, radius, RetroSkin.DarkShadow);

        // Hour markers — fat ticks every 3 hours, thin between.
        for (int t = 0; t < 12; t++)
        {
            double ang = -Math.PI / 2 + t * (2 * Math.PI / 12);
            float x1 = cx + (float)Math.Cos(ang) * (radius - 12);
            float y1 = cy + (float)Math.Sin(ang) * (radius - 12);
            float x2 = cx + (float)Math.Cos(ang) * (radius - 2);
            float y2 = cy + (float)Math.Sin(ang) * (radius - 2);
            float thick = (t % 3 == 0) ? 3f : 1.5f;
            Raylib.DrawLineEx(new Vector2(x1, y1), new Vector2(x2, y2), thick, RetroSkin.BodyText);
        }

        // Hour numbers
        for (int t = 1; t <= 12; t++)
        {
            double ang = -Math.PI / 2 + t * (2 * Math.PI / 12);
            float nx = cx + (float)Math.Cos(ang) * (radius - 28);
            float ny = cy + (float)Math.Sin(ang) * (radius - 28);
            string s = t.ToString();
            int nw = FontManager.MeasureText(s, 16);
            FontManager.DrawText(s, (int)(nx - nw / 2), (int)(ny - 8), 16, RetroSkin.BodyText);
        }

        // Minute hand
        double mAng = -Math.PI / 2 + _minute * (2 * Math.PI / 60);
        float mx = cx + (float)Math.Cos(mAng) * (radius - 16);
        float my = cy + (float)Math.Sin(mAng) * (radius - 16);
        Raylib.DrawLineEx(new Vector2(cx, cy), new Vector2(mx, my), 3f, RetroSkin.BodyText);

        // Hour hand — shorter and accounting for the minute fraction so
        // the hour hand sits between numbers as the minute progresses.
        double hAng = -Math.PI / 2 + ((_hour % 12) + _minute / 60.0) * (2 * Math.PI / 12);
        float hx = cx + (float)Math.Cos(hAng) * (radius - 40);
        float hy = cy + (float)Math.Sin(hAng) * (radius - 40);
        Raylib.DrawLineEx(new Vector2(cx, cy), new Vector2(hx, hy), 5f, RetroSkin.BodyText);

        // Center pin
        Raylib.DrawCircle((int)cx, (int)cy, 4f, RetroSkin.DarkShadow);
    }
}
