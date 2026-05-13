using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Math Flashcards — pick an operator (+, −, ×, ÷) and a difficulty band,
/// then answer a stream of randomly generated questions by typing the
/// number. Each correct answer adds to the streak; each wrong answer
/// resets the streak but reveals the right answer so the player learns.
///
/// Inspired by the MATH category of the "201 Learning Games" collection.
/// </summary>
public class MathFlashcardsActivity : IActivity
{
    public Vector2 PanelSize => new(440, 360);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int Pad = 14;

    private enum Op { Add, Sub, Mul, Div }
    private enum Diff { Easy, Medium, Hard }

    private Op _op = Op.Add;
    private Diff _diff = Diff.Easy;
    private int _a, _b, _correct;
    private string _input = "";
    private int _streak;
    private int _best;
    private int _wrong;
    private string _feedback = "Pick a mode and start answering!";
    private bool _showAnswer;
    private float _showAnswerTimer;
    private readonly Random _rng = new();

    public void Load() => NewQuestion();
    public void Close() => IsFinished = true;

    private (int min, int max) Range() => (_op, _diff) switch
    {
        (Op.Add, Diff.Easy) => (1, 10),
        (Op.Add, Diff.Medium) => (1, 50),
        (Op.Add, Diff.Hard) => (10, 200),
        (Op.Sub, Diff.Easy) => (1, 10),
        (Op.Sub, Diff.Medium) => (1, 50),
        (Op.Sub, Diff.Hard) => (10, 200),
        (Op.Mul, Diff.Easy) => (1, 6),
        (Op.Mul, Diff.Medium) => (1, 12),
        (Op.Mul, Diff.Hard) => (1, 25),
        (Op.Div, Diff.Easy) => (1, 6),
        (Op.Div, Diff.Medium) => (1, 12),
        (Op.Div, Diff.Hard) => (1, 20),
        _ => (1, 10),
    };

    private void NewQuestion()
    {
        var (min, max) = Range();
        _a = _rng.Next(min, max + 1);
        _b = _rng.Next(min, max + 1);
        switch (_op)
        {
            case Op.Add: _correct = _a + _b; break;
            // For subtraction, ensure a ≥ b so the answer is non-negative —
            // matches how introductory worksheets present the operator.
            case Op.Sub:
                if (_a < _b) (_a, _b) = (_b, _a);
                _correct = _a - _b;
                break;
            case Op.Mul: _correct = _a * _b; break;
            // For division, construct the question from the quotient so it
            // always divides evenly. Avoids "what's 17/4?" type answers
            // before the player has learned remainders.
            case Op.Div:
                int q = _rng.Next(min, max + 1);
                int divisor = _rng.Next(Math.Max(1, min), max + 1);
                _a = q * divisor;
                _b = divisor;
                _correct = q;
                break;
        }
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
            _op == Op.Add ? "+ ✓" : "+",
            _op == Op.Sub ? "− ✓" : "−",
            _op == Op.Mul ? "× ✓" : "×",
            _op == Op.Div ? "÷ ✓" : "÷",
            _diff switch { Diff.Easy => "Easy", Diff.Medium => "Med", _ => "Hard" },
            "Skip",
        };
        int hit = RetroWidgets.MenuBarHitTest(menuBar, items, local, leftPressed);
        if (hit == 0) { _op = Op.Add; NewQuestion(); return; }
        if (hit == 1) { _op = Op.Sub; NewQuestion(); return; }
        if (hit == 2) { _op = Op.Mul; NewQuestion(); return; }
        if (hit == 3) { _op = Op.Div; NewQuestion(); return; }
        if (hit == 4)
        {
            _diff = (Diff)(((int)_diff + 1) % 3);
            NewQuestion();
            return;
        }
        if (hit == 5) { NewQuestion(); _feedback = "Skipped."; return; }

        if (_showAnswer)
        {
            _showAnswerTimer += delta;
            if (_showAnswerTimer > 1.5f) { _showAnswerTimer = 0; NewQuestion(); }
            return;
        }

        // Number input via the char stream — Raylib hands us already-translated
        // codepoints so numpad/main row both work without explicit key checks.
        int ch = Raylib.GetCharPressed();
        while (ch > 0)
        {
            if (ch == '-' && _input.Length == 0) _input = "-";
            else if (ch >= '0' && ch <= '9' && _input.Length < 6) _input += (char)ch;
            ch = Raylib.GetCharPressed();
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && _input.Length > 0)
            _input = _input[..^1];
        if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
            Submit();
    }

    private void Submit()
    {
        if (!int.TryParse(_input, out var typed)) { _feedback = "Type a number, then press Enter."; return; }
        if (typed == _correct)
        {
            _streak++;
            if (_streak > _best) _best = _streak;
            _feedback = "Correct!";
            NewQuestion();
        }
        else
        {
            _streak = 0;
            _wrong++;
            _feedback = "Not quite — the answer is " + _correct;
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
        RetroWidgets.DrawTitleBarVisual(titleBar, "Math Flashcards", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        var items = new[] {
            _op == Op.Add ? "+ ✓" : "+",
            _op == Op.Sub ? "− ✓" : "−",
            _op == Op.Mul ? "× ✓" : "×",
            _op == Op.Div ? "÷ ✓" : "÷",
            _diff switch { Diff.Easy => "Easy", Diff.Medium => "Med", _ => "Hard" },
            "Skip",
        };
        RetroWidgets.MenuBarVisual(menuBar, items, -1);

        // Question card
        var card = new Rectangle(
            panelOffset.X + FrameInset + Pad,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight
                + RetroWidgets.MenuBarHeight + Pad,
            PanelSize.X - 2 * (FrameInset + Pad), 180);
        RetroSkin.DrawSunken(card, RetroSkin.SunkenBg);

        string opSym = _op switch { Op.Add => "+", Op.Sub => "−", Op.Mul => "×", _ => "÷" };
        string q = $"{_a} {opSym} {_b} = {(_input.Length == 0 ? "?" : _input)}";
        int qw = FontManager.MeasureText(q, 40);
        FontManager.DrawText(q,
            (int)(card.X + (card.Width - qw) / 2),
            (int)(card.Y + 40),
            40, _showAnswer ? RetroSkin.DisabledText : RetroSkin.BodyText);

        // Feedback line under the question.
        int fw = FontManager.MeasureText(_feedback, 16);
        FontManager.DrawText(_feedback,
            (int)(card.X + (card.Width - fw) / 2),
            (int)(card.Y + 120),
            16, _showAnswer ? RetroSkin.TitleActive : RetroSkin.BodyText);

        // Mini score panel
        FontManager.DrawText($"Streak: {_streak}    Best: {_best}    Wrong: {_wrong}",
            (int)(card.X), (int)(card.Y + card.Height + 12), 14, RetroSkin.BodyText);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, "Type the answer, then press Enter.",
            $"{_op}  •  {_diff}");
    }
}
