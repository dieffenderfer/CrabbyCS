using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Go Figure! — make the target number from a hand of digits and operators.
/// Click digits / operators to build an expression, Eval to check. Standard
/// arithmetic precedence applies.
/// </summary>
public class GoFigureActivity : IActivity
{
    private const int FrameInset = 3;
    private const int Margin = 18;
    private const int Tile = 44;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + 9 * (Tile + 6),
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + 30 + 36 + Tile + 50 + Margin + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly RetroHelp _help = new()
    {
        Title = "Go Figure! — How to play",
        Lines = new[]
        {
            "Hit the target number using the digit and operator tiles.",
            "Click tiles to build an expression at the top.",
            "Standard precedence: * and / before + and -.",
            "Press Eval (or Enter) to grade the expression.",
            "Backspace removes the last tile; Clear empties the line.",
            "Stuck? Hint reveals one valid solution for this hand.",
        },
    };

    private List<string> _hand = new();
    private bool[] _used = Array.Empty<bool>();
    private List<int> _expr = new();
    private int _target;
    private string _msg = "";
    private int _score;
    private readonly Random _rng = new();

    // Solved-by-construction: every deal carries a known-good answer
    // so we never hand the player an unsolvable puzzle. Stored as the
    // ordered list of tile indices that produces _target.
    private List<int> _knownSolution = new();

    public void Load() => Deal();

    private void Deal()
    {
        // Re-roll up to a few times until brute-force confirms the deal
        // has at least one valid expression equal to the target. The
        // search space is small (~6k expressions) so even worst-case
        // re-rolls finish well under a frame.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            _hand.Clear();
            for (int i = 0; i < 5; i++) _hand.Add(_rng.Next(1, 10).ToString());
            var ops = new[] { "+", "-", "*", "/" };
            for (int i = 0; i < 4; i++) _hand.Add(ops[_rng.Next(ops.Length)]);
            for (int i = _hand.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_hand[i], _hand[j]) = (_hand[j], _hand[i]);
            }
            _target = _rng.Next(10, 50);
            var sol = FindSolution(_target);
            if (sol != null)
            {
                _knownSolution = sol;
                break;
            }
            // Same hand might solve a different target — try a few before
            // discarding the hand entirely.
            for (int t = 0; t < 8; t++)
            {
                _target = _rng.Next(10, 50);
                sol = FindSolution(_target);
                if (sol != null) { _knownSolution = sol; break; }
            }
            if (_knownSolution.Count > 0) break;
        }

        _used = new bool[_hand.Count];
        _expr.Clear();
        _msg = $"Make {_target}";
    }

    /// <summary>
    /// Brute-force search for an ordered list of tile indices that forms
    /// a valid expression equaling <paramref name="target"/>. Returns
    /// <c>null</c> when no arrangement of the current hand works.
    /// </summary>
    private List<int>? FindSolution(int target)
    {
        // Partition the hand into digit indices and operator indices so
        // we can enumerate the alternating digit-op-digit-op-... shape.
        var digitIdx = new List<int>();
        var opIdx = new List<int>();
        for (int i = 0; i < _hand.Count; i++)
        {
            if (char.IsDigit(_hand[i][0])) digitIdx.Add(i);
            else opIdx.Add(i);
        }
        // Try expressions using k digits (and k-1 ops). k ranges 1..|digits|.
        for (int k = 1; k <= digitIdx.Count; k++)
        {
            var digitPerm = new int[k];
            var opPerm = new int[Math.Max(0, k - 1)];
            var digitUsed = new bool[digitIdx.Count];
            var opUsed = new bool[opIdx.Count];
            var found = TrySolve(digitIdx, opIdx, digitPerm, opPerm,
                digitUsed, opUsed, 0, target);
            if (found != null) return found;
        }
        return null;
    }

    private List<int>? TrySolve(List<int> digitIdx, List<int> opIdx,
        int[] digitPerm, int[] opPerm, bool[] digitUsed, bool[] opUsed,
        int slot, int target)
    {
        int k = digitPerm.Length;
        if (slot == 2 * k - 1 || (k == 1 && slot == 1))
        {
            // Build the expression string from the permutations.
            var ordered = new List<int>();
            for (int i = 0; i < k; i++)
            {
                ordered.Add(digitPerm[i]);
                if (i < k - 1) ordered.Add(opPerm[i]);
            }
            string s = string.Concat(ordered.Select(idx => _hand[idx]));
            try
            {
                double v = SimpleEval(s);
                if (Math.Abs(v - target) < 1e-9) return ordered;
            }
            catch { }
            return null;
        }
        bool digitSlot = (slot % 2) == 0;
        if (digitSlot)
        {
            int pos = slot / 2;
            for (int i = 0; i < digitIdx.Count; i++)
            {
                if (digitUsed[i]) continue;
                digitUsed[i] = true;
                digitPerm[pos] = digitIdx[i];
                var r = TrySolve(digitIdx, opIdx, digitPerm, opPerm,
                    digitUsed, opUsed, slot + 1, target);
                digitUsed[i] = false;
                if (r != null) return r;
            }
        }
        else
        {
            int pos = slot / 2;
            for (int i = 0; i < opIdx.Count; i++)
            {
                if (opUsed[i]) continue;
                opUsed[i] = true;
                opPerm[pos] = opIdx[i];
                var r = TrySolve(digitIdx, opIdx, digitPerm, opPerm,
                    digitUsed, opUsed, slot + 1, target);
                opUsed[i] = false;
                if (r != null) return r;
            }
        }
        return null;
    }

    private void ShowHint()
    {
        if (_knownSolution.Count == 0) { _msg = "No hint available."; return; }
        _msg = "Hint: " + string.Concat(_knownSolution.Select(i => _hand[i]));
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
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Eval", "Backspace", "Clear", "Hint", "Help" }, local, leftPressed))
        {
            case 0: Deal(); return;
            case 1: Eval(); return;
            case 2: Back(); return;
            case 3: ClearExpr(); return;
            case 4: ShowHint(); return;
            case 5: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (Raylib.IsKeyPressed(KeyboardKey.Enter)) { Eval(); return; }
        if (Raylib.IsKeyPressed(KeyboardKey.Backspace)) { Back(); return; }

        if (leftPressed)
        {
            for (int i = 0; i < _hand.Count; i++)
            {
                if (_used[i]) continue;
                if (RetroSkin.PointInRect(local, TileRect(i)))
                {
                    _used[i] = true;
                    _expr.Add(i);
                    return;
                }
            }
        }
    }

    private Rectangle TileRect(int i)
    {
        float bx = FrameInset + Margin;
        float by = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin + 30 + 36;
        return new Rectangle(bx + i * (Tile + 6), by, Tile, Tile);
    }

    private void Back()
    {
        if (_expr.Count == 0) return;
        int last = _expr[^1];
        _expr.RemoveAt(_expr.Count - 1);
        _used[last] = false;
    }

    private void ClearExpr()
    {
        foreach (var i in _expr) _used[i] = false;
        _expr.Clear();
    }

    private string ExprText() => string.Concat(_expr.Select(i => _hand[i]));

    private void Eval()
    {
        var s = ExprText();
        if (s.Length == 0) { _msg = "Empty"; return; }
        try
        {
            double v = SimpleEval(s);
            if (Math.Abs(v - _target) < 1e-9)
            {
                _msg = $"= {v} ✓";
                _score++;
            }
            else _msg = $"= {v}  (need {_target})";
        }
        catch
        {
            _msg = "Invalid";
        }
    }

    /// <summary>Tiny shunting-yard for + - * / on integer/float literals.</summary>
    private static double SimpleEval(string s)
    {
        var output = new List<string>();
        var ops = new Stack<char>();
        int Prec(char c) => (c == '+' || c == '-') ? 1 : 2;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsDigit(c))
            {
                int j = i;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                output.Add(s[i..j]);
                i = j;
                continue;
            }
            if ("+-*/".Contains(c))
            {
                while (ops.Count > 0 && Prec(ops.Peek()) >= Prec(c)) output.Add(ops.Pop().ToString());
                ops.Push(c);
                i++;
                continue;
            }
            i++;
        }
        while (ops.Count > 0) output.Add(ops.Pop().ToString());

        var stack = new Stack<double>();
        foreach (var t in output)
        {
            if (double.TryParse(t, out var d)) stack.Push(d);
            else
            {
                double b = stack.Pop(), a = stack.Pop();
                stack.Push(t switch
                {
                    "+" => a + b, "-" => a - b, "*" => a * b,
                    "/" => b == 0 ? throw new DivideByZeroException() : a / b,
                    _ => throw new InvalidOperationException(),
                });
            }
        }
        if (stack.Count != 1) throw new InvalidOperationException();
        return stack.Pop();
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Go Figure!", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Eval", "Backspace", "Clear", "Hint", "Help" }, -1);

        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;

        // Target
        string targetTxt = $"Target  {_target}";
        int tw = RetroSkin.MeasureText(targetTxt, 22);
        RetroSkin.DrawText(targetTxt,
            (int)(panelOffset.X + (PanelSize.X - tw) / 2),
            (int)by, RetroSkin.BodyText, 22);

        // Expression box
        var box = new Rectangle(panelOffset.X + FrameInset + Margin, by + 30,
            PanelSize.X - 2 * (FrameInset + Margin), 32);
        RetroSkin.DrawSunken(box, RetroSkin.SunkenBg);
        var ex = ExprText();
        int ew = RetroSkin.MeasureText(ex, 24);
        RetroSkin.DrawText(ex,
            (int)(box.X + (box.Width - ew) / 2),
            (int)(box.Y + 4),
            RetroSkin.BodyText, 24);

        // Hand tiles
        for (int i = 0; i < _hand.Count; i++)
        {
            var r = TileRect(i);
            var abs = new Rectangle(panelOffset.X + r.X, panelOffset.Y + r.Y, r.Width, r.Height);
            if (_used[i])
            {
                Raylib.DrawRectangleRec(abs, RetroSkin.Face);
                Raylib.DrawRectangleLinesEx(abs, 1, RetroSkin.Shadow);
            }
            else
            {
                RetroSkin.DrawRaised(abs);
            }
            int lw = RetroSkin.MeasureText(_hand[i], 24);
            RetroSkin.DrawText(_hand[i],
                (int)(abs.X + (abs.Width - lw) / 2),
                (int)(abs.Y + 6),
                _used[i] ? RetroSkin.DisabledText : RetroSkin.BodyText, 24);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _msg, $"Solved: {_score}");

        _help.Draw(panelOffset, PanelSize);
    }

    public void Close() { }
}
