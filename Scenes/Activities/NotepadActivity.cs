using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Minimal plain-text Notepad activity. Auto-saves on every keystroke so the
/// user never has to think about Ctrl-S. One persistent buffer (notepad.txt)
/// lives in the app save directory; New empties the buffer and overwrites
/// the file on next change. Newlines are stored as '\n'.
/// </summary>
public class NotepadActivity : IActivity
{
    public Vector2 PanelSize => new(560, 420);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int Padding = 10;
    private const int FontSize = 16;
    private const int LineH = 20;
    private const string SaveFileName = "notepad.txt";

    private readonly List<string> _lines = new() { "" };
    private int _row;
    private int _col;
    private float _scrollY;
    private float _cursorBlink;

    // Repeat-on-hold for navigation and deletion. Same timings the status
    // bubble uses so the feel matches the rest of the OS.
    private const float KeyRepeatDelay = 0.40f;
    private const float KeyRepeatRate = 0.04f;
    private float _bkspHeld;
    private float _delHeld;
    private float _navHeld;
    private KeyboardKey _navKey;
    private string _status = "";

    public void Load()
    {
        var path = Path.Combine(SaveManager.SaveDirectory, SaveFileName);
        if (File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);
                SetText(text);
                _status = $"Loaded {path}";
            }
            catch (Exception ex) { _status = "Load failed: " + ex.Message; }
        }
        else
        {
            _status = "(new buffer — auto-saves to " + SaveFileName + ")";
        }
    }

    public void Close() => Save();

    private void SetText(string text)
    {
        _lines.Clear();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            _lines.Add(line);
        if (_lines.Count == 0) _lines.Add("");
        _row = Math.Min(_row, _lines.Count - 1);
        _col = Math.Min(_col, _lines[_row].Length);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(SaveManager.SaveDirectory);
            var path = Path.Combine(SaveManager.SaveDirectory, SaveFileName);
            File.WriteAllText(path, string.Join("\n", _lines));
        }
        catch (Exception ex) { _status = "Save failed: " + ex.Message; }
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        _cursorBlink += delta;

        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Save Copy" }, local, leftPressed))
        {
            case 0: NewBuffer(); return;
            case 1: SaveCopy(); return;
        }

        // Click in editor places cursor.
        var editor = EditorRect();
        if (leftPressed && RetroSkin.PointInRect(local, editor))
        {
            HitTestToCursor(local, editor);
            _cursorBlink = 0;
        }

        // Scroll
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0 && RetroSkin.PointInRect(local, editor))
        {
            _scrollY -= wheel * LineH * 3;
            ClampScroll();
        }

        HandleKeys(delta);
    }

    private void NewBuffer()
    {
        _lines.Clear();
        _lines.Add("");
        _row = 0;
        _col = 0;
        _scrollY = 0;
        Save();
        _status = "New buffer.";
    }

    private void SaveCopy()
    {
        // Plain-old timestamped sidecar — gives the user a way to keep an
        // older draft without leaving the notepad. No file picker; the path
        // is shown in the status bar so the user knows where it landed.
        try
        {
            Directory.CreateDirectory(SaveManager.SaveDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(SaveManager.SaveDirectory, $"notepad-{stamp}.txt");
            File.WriteAllText(path, string.Join("\n", _lines));
            _status = "Saved copy: notepad-" + stamp + ".txt";
        }
        catch (Exception ex) { _status = "Save copy failed: " + ex.Message; }
    }

    private void HandleKeys(float delta)
    {
        bool dirty = false;

        // Character input — Raylib hands us already-translated codepoints
        // so layout / shift modifiers are handled by the OS.
        int ch = Raylib.GetCharPressed();
        while (ch > 0)
        {
            InsertChar((char)ch);
            dirty = true;
            ch = Raylib.GetCharPressed();
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Enter)
            || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
        {
            InsertNewline();
            dirty = true;
        }

        if (HandleRepeated(KeyboardKey.Backspace, delta, ref _bkspHeld))
        {
            Backspace();
            dirty = true;
        }
        if (HandleRepeated(KeyboardKey.Delete, delta, ref _delHeld))
        {
            DeleteForward();
            dirty = true;
        }

        // Navigation keys share a single held-timer so only one direction
        // repeats at a time — simpler than per-key timers and matches how
        // most editors feel.
        if (Raylib.IsKeyPressed(KeyboardKey.Left)) { MoveLeft(); _navKey = KeyboardKey.Left; _navHeld = 0; _cursorBlink = 0; }
        if (Raylib.IsKeyPressed(KeyboardKey.Right)) { MoveRight(); _navKey = KeyboardKey.Right; _navHeld = 0; _cursorBlink = 0; }
        if (Raylib.IsKeyPressed(KeyboardKey.Up)) { MoveUp(); _navKey = KeyboardKey.Up; _navHeld = 0; _cursorBlink = 0; }
        if (Raylib.IsKeyPressed(KeyboardKey.Down)) { MoveDown(); _navKey = KeyboardKey.Down; _navHeld = 0; _cursorBlink = 0; }
        if (Raylib.IsKeyPressed(KeyboardKey.Home)) { _col = 0; _cursorBlink = 0; }
        if (Raylib.IsKeyPressed(KeyboardKey.End)) { _col = _lines[_row].Length; _cursorBlink = 0; }
        if (Raylib.IsKeyDown(_navKey))
        {
            _navHeld += delta;
            if (_navHeld > KeyRepeatDelay)
            {
                _navHeld -= KeyRepeatRate;
                switch (_navKey)
                {
                    case KeyboardKey.Left: MoveLeft(); break;
                    case KeyboardKey.Right: MoveRight(); break;
                    case KeyboardKey.Up: MoveUp(); break;
                    case KeyboardKey.Down: MoveDown(); break;
                }
                _cursorBlink = 0;
            }
        }

        EnsureCursorVisible();
        if (dirty) { Save(); _cursorBlink = 0; }
    }

    private bool HandleRepeated(KeyboardKey key, float delta, ref float held)
    {
        if (Raylib.IsKeyPressed(key)) { held = 0; return true; }
        if (Raylib.IsKeyDown(key))
        {
            held += delta;
            if (held > KeyRepeatDelay)
            {
                held -= KeyRepeatRate;
                return true;
            }
        }
        else held = 0;
        return false;
    }

    private void InsertChar(char c)
    {
        var s = _lines[_row];
        _lines[_row] = s.Insert(_col, c.ToString());
        _col++;
    }

    private void InsertNewline()
    {
        var cur = _lines[_row];
        string left = cur[.._col];
        string right = cur[_col..];
        _lines[_row] = left;
        _lines.Insert(_row + 1, right);
        _row++;
        _col = 0;
    }

    private void Backspace()
    {
        if (_col > 0)
        {
            _lines[_row] = _lines[_row].Remove(_col - 1, 1);
            _col--;
        }
        else if (_row > 0)
        {
            int prevLen = _lines[_row - 1].Length;
            _lines[_row - 1] += _lines[_row];
            _lines.RemoveAt(_row);
            _row--;
            _col = prevLen;
        }
    }

    private void DeleteForward()
    {
        if (_col < _lines[_row].Length)
        {
            _lines[_row] = _lines[_row].Remove(_col, 1);
        }
        else if (_row < _lines.Count - 1)
        {
            _lines[_row] += _lines[_row + 1];
            _lines.RemoveAt(_row + 1);
        }
    }

    private void MoveLeft()
    {
        if (_col > 0) _col--;
        else if (_row > 0) { _row--; _col = _lines[_row].Length; }
    }

    private void MoveRight()
    {
        if (_col < _lines[_row].Length) _col++;
        else if (_row < _lines.Count - 1) { _row++; _col = 0; }
    }

    private void MoveUp()
    {
        if (_row > 0) { _row--; _col = Math.Min(_col, _lines[_row].Length); }
    }

    private void MoveDown()
    {
        if (_row < _lines.Count - 1) { _row++; _col = Math.Min(_col, _lines[_row].Length); }
    }

    private Rectangle EditorRect()
    {
        float top = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 4;
        float bottom = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 4;
        return new Rectangle(FrameInset + 4, top,
            PanelSize.X - 2 * FrameInset - 8, bottom - top);
    }

    private int VisibleLineCount() => (int)(EditorRect().Height / LineH);

    private void ClampScroll()
    {
        float maxScroll = Math.Max(0, _lines.Count * LineH - EditorRect().Height + LineH);
        _scrollY = Math.Clamp(_scrollY, 0, maxScroll);
    }

    private void EnsureCursorVisible()
    {
        float cursorY = _row * LineH;
        if (cursorY < _scrollY) _scrollY = cursorY;
        else if (cursorY + LineH > _scrollY + EditorRect().Height)
            _scrollY = cursorY + LineH - EditorRect().Height;
        ClampScroll();
    }

    private void HitTestToCursor(Vector2 local, Rectangle editor)
    {
        int row = (int)((local.Y - editor.Y + _scrollY) / LineH);
        row = Math.Clamp(row, 0, _lines.Count - 1);
        _row = row;
        var line = _lines[_row];
        float xOff = local.X - editor.X - Padding;
        // Best-fit by measuring incrementally — small lines, so the linear
        // scan is fine.
        _col = line.Length;
        for (int i = 0; i <= line.Length; i++)
        {
            float w = FontManager.MeasureText(line[..i], FontSize);
            if (w >= xOff) { _col = i; break; }
        }
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Notepad", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Save Copy" }, -1);

        var editor = EditorRect();
        var editorAbs = new Rectangle(panelOffset.X + editor.X, panelOffset.Y + editor.Y,
            editor.Width, editor.Height);
        RetroSkin.DrawSunken(editorAbs, RetroSkin.SunkenBg);

        // Text — manual y-clip (no scissor; see project memory on Retina bugs).
        int textLeft = (int)(editorAbs.X + Padding);
        float topY = editorAbs.Y + 4 - _scrollY;
        for (int i = 0; i < _lines.Count; i++)
        {
            float y = topY + i * LineH;
            if (y + LineH < editorAbs.Y) continue;
            if (y > editorAbs.Y + editorAbs.Height) break;
            FontManager.DrawText(_lines[i], textLeft, (int)y, FontSize, RetroSkin.BodyText);
        }

        // Cursor (steady-on for the first half of each blink cycle).
        if (((int)(_cursorBlink * 2)) % 2 == 0)
        {
            float cx = textLeft + FontManager.MeasureText(_lines[_row][.._col], FontSize);
            float cy = topY + _row * LineH;
            if (cy >= editorAbs.Y - LineH && cy <= editorAbs.Y + editorAbs.Height)
            {
                Raylib.DrawLine((int)cx + 1, (int)cy, (int)cx + 1,
                    (int)(cy + FontSize), RetroSkin.BodyText);
            }
        }

        // Status bar — line/col and any save/load status note.
        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string left = $"Ln {_row + 1}, Col {_col + 1}";
        RetroWidgets.StatusBar(status, _status, left);
    }
}
