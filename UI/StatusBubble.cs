using MouseHouse.Core;
using System.Numerics;
using Raylib_cs;

namespace MouseHouse.UI;

public class StatusBubble
{
    public bool Visible { get; private set; }
    public bool IsEditing { get; private set; }

    private string _text = "";
    private int _cursor;
    private int _selAnchor = -1;
    private bool _cursorVisible;
    private float _cursorTimer;
    private float _deleteHeldTime;
    private bool _draggingSelection;

    private const int FontSize = 14;
    private const int LineHeight = FontSize + 4;
    private const int PaddingX = 10;
    private const int PaddingY = 6;
    private const int CloseButtonSize = 16;
    private const int MaxLineWidth = 200;
    private const int MaxLines = 4;
    private const float CursorBlinkRate = 0.53f;
    private const float DeleteDelay = 0.4f;
    private const float DeleteRepeat = 0.05f;

    private static readonly Color BgColor = new(40, 40, 45, 220);
    private static readonly Color BorderColor = new(60, 60, 65, 240);
    private static readonly Color TextColor = new(230, 230, 230, 255);
    private static readonly Color CloseColor = new(180, 80, 80, 255);
    private static readonly Color CloseHoverColor = new(220, 100, 100, 255);
    private static readonly Color CursorColor = new(200, 200, 200, 255);
    private static readonly Color EditBorderColor = new(70, 130, 200, 200);
    private static readonly Color PlaceholderColor = new(150, 150, 150, 180);
    private static readonly Color SelectionColor = new(50, 90, 160, 180);

    private Rectangle _bubbleRect;
    private Rectangle _closeRect;
    private bool _closeHovered;
    private float _bubbleX, _bubbleY;
    private List<string> _cachedLines = new() { "" };
    private List<int> _lineStartIndices = new() { 0 };

    private bool HasSelection => _selAnchor >= 0 && _selAnchor != _cursor;
    private int SelMin => Math.Min(_cursor, _selAnchor);
    private int SelMax => Math.Max(_cursor, _selAnchor);

    private static bool Cmd => OperatingSystem.IsMacOS()
        ? Raylib.IsKeyDown(KeyboardKey.LeftSuper) || Raylib.IsKeyDown(KeyboardKey.RightSuper)
        : Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);

    private static bool Shift =>
        Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);

    public void StartEditing()
    {
        Visible = true;
        IsEditing = true;
        _text = "";
        _cursor = 0;
        _selAnchor = -1;
        _cursorTimer = 0;
        _cursorVisible = true;
        _deleteHeldTime = 0;
        _draggingSelection = false;
    }

    public void Hide()
    {
        Visible = false;
        IsEditing = false;
        _text = "";
        _cursor = 0;
        _selAnchor = -1;
    }

    public bool ContainsPoint(Vector2 point)
    {
        if (!Visible) return false;
        return Raylib.CheckCollisionPointRec(point, _bubbleRect)
            || Raylib.CheckCollisionPointRec(point, _closeRect);
    }

    public bool Update(float delta, Vector2 mousePos, bool leftPressed)
    {
        if (!Visible) return false;

        _closeHovered = Raylib.CheckCollisionPointRec(mousePos, _closeRect);
        bool leftDown = Raylib.IsMouseButtonDown(MouseButton.Left);
        bool leftReleased = Raylib.IsMouseButtonReleased(MouseButton.Left);

        if (leftPressed)
        {
            if (_closeHovered)
            {
                Hide();
                return true;
            }

            bool inBubble = Raylib.CheckCollisionPointRec(mousePos, _bubbleRect);

            if (inBubble && !IsEditing)
            {
                IsEditing = true;
                _cursor = _text.Length;
                _selAnchor = -1;
                ResetBlink();
                return true;
            }

            if (inBubble && IsEditing)
            {
                int pos = HitTestPosition(mousePos);
                if (Shift)
                {
                    if (_selAnchor < 0) _selAnchor = _cursor;
                    _cursor = pos;
                }
                else
                {
                    _cursor = pos;
                    _selAnchor = pos;
                    _draggingSelection = true;
                }
                ResetBlink();
                return true;
            }

            if (!inBubble && IsEditing)
            {
                IsEditing = false;
                _selAnchor = -1;
                if (_text.Length == 0) Hide();
                return false;
            }
        }

        if (_draggingSelection && leftDown && IsEditing)
        {
            _cursor = HitTestPosition(mousePos);
            ResetBlink();
        }

        if (leftReleased)
        {
            _draggingSelection = false;
            if (_selAnchor == _cursor) _selAnchor = -1;
        }

        if (IsEditing)
        {
            _cursorTimer += delta;
            if (_cursorTimer >= CursorBlinkRate)
            {
                _cursorTimer -= CursorBlinkRate;
                _cursorVisible = !_cursorVisible;
            }

            // Cmd+A select all
            if (Cmd && Raylib.IsKeyPressed(KeyboardKey.A))
            {
                _selAnchor = 0;
                _cursor = _text.Length;
                ResetBlink();
            }
            // Cmd+C copy
            else if (Cmd && Raylib.IsKeyPressed(KeyboardKey.C))
            {
                if (HasSelection)
                    Raylib.SetClipboardText(_text[SelMin..SelMax]);
            }
            // Cmd+X cut
            else if (Cmd && Raylib.IsKeyPressed(KeyboardKey.X))
            {
                if (HasSelection)
                {
                    Raylib.SetClipboardText(_text[SelMin..SelMax]);
                    DeleteSelection();
                }
            }
            // Cmd+V paste
            else if (Cmd && Raylib.IsKeyPressed(KeyboardKey.V))
            {
                var clip = Raylib.GetClipboardText_();
                if (!string.IsNullOrEmpty(clip))
                {
                    clip = clip.Replace("\r", "").Replace("\n", " ");
                    InsertText(clip);
                }
            }
            else
            {
                // Character input
                int key = Raylib.GetCharPressed();
                while (key > 0)
                {
                    if (key >= 32 && key <= 126)
                        InsertText(((char)key).ToString());
                    key = Raylib.GetCharPressed();
                }

                HandleDeleteKeys(delta);
                HandleArrowKeys();
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
            {
                IsEditing = false;
                _selAnchor = -1;
                if (_text.Length == 0) Hide();
                return true;
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Escape))
            {
                IsEditing = false;
                _selAnchor = -1;
                if (_text.Length == 0) Hide();
                return true;
            }
        }

        return false;
    }

    private void InsertText(string s)
    {
        if (HasSelection) DeleteSelection();
        string candidate = _text[.._cursor] + s + _text[_cursor..];
        if (WrapText(candidate).Count > MaxLines) return;
        _text = candidate;
        _cursor += s.Length;
        _selAnchor = -1;
        ResetBlink();
    }

    private void DeleteSelection()
    {
        if (!HasSelection) return;
        int min = SelMin, max = SelMax;
        _text = _text[..min] + _text[max..];
        _cursor = min;
        _selAnchor = -1;
        ResetBlink();
    }

    private void HandleDeleteKeys(float delta)
    {
        bool bksp = Raylib.IsKeyDown(KeyboardKey.Backspace);
        bool del = Raylib.IsKeyDown(KeyboardKey.Delete);

        if (!bksp && !del)
        {
            _deleteHeldTime = 0;
            return;
        }

        bool pressed = Raylib.IsKeyPressed(KeyboardKey.Backspace) || Raylib.IsKeyPressed(KeyboardKey.Delete);
        bool doDelete = false;

        if (pressed)
        {
            doDelete = true;
            _deleteHeldTime = 0;
        }
        else
        {
            _deleteHeldTime += delta;
            if (_deleteHeldTime >= DeleteDelay)
            {
                _deleteHeldTime -= DeleteRepeat;
                doDelete = true;
            }
        }

        if (!doDelete) return;

        if (HasSelection)
        {
            DeleteSelection();
        }
        else if (bksp && _cursor > 0)
        {
            _text = _text[..(_cursor - 1)] + _text[_cursor..];
            _cursor--;
            ResetBlink();
        }
        else if (del && _cursor < _text.Length)
        {
            _text = _text[.._cursor] + _text[(_cursor + 1)..];
            ResetBlink();
        }
    }

    private void HandleArrowKeys()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Left))
        {
            if (Shift)
            {
                if (_selAnchor < 0) _selAnchor = _cursor;
                if (_cursor > 0) _cursor--;
            }
            else
            {
                if (HasSelection)
                    _cursor = SelMin;
                else if (_cursor > 0)
                    _cursor--;
                _selAnchor = -1;
            }
            ResetBlink();
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Right))
        {
            if (Shift)
            {
                if (_selAnchor < 0) _selAnchor = _cursor;
                if (_cursor < _text.Length) _cursor++;
            }
            else
            {
                if (HasSelection)
                    _cursor = SelMax;
                else if (_cursor < _text.Length)
                    _cursor++;
                _selAnchor = -1;
            }
            ResetBlink();
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Home))
        {
            if (Shift)
            {
                if (_selAnchor < 0) _selAnchor = _cursor;
            }
            else
                _selAnchor = -1;
            _cursor = 0;
            ResetBlink();
        }

        if (Raylib.IsKeyPressed(KeyboardKey.End))
        {
            if (Shift)
            {
                if (_selAnchor < 0) _selAnchor = _cursor;
            }
            else
                _selAnchor = -1;
            _cursor = _text.Length;
            ResetBlink();
        }

        // Up/Down arrow to move between wrapped lines
        if (Raylib.IsKeyPressed(KeyboardKey.Up) || Raylib.IsKeyPressed(KeyboardKey.Down))
        {
            UpdateLineCache();
            var (line, col) = GetLineCol(_cursor);
            int targetLine = Raylib.IsKeyPressed(KeyboardKey.Up) ? line - 1 : line + 1;
            if (targetLine >= 0 && targetLine < _cachedLines.Count)
            {
                int maxCol = _cachedLines[targetLine].Length;
                int newPos = _lineStartIndices[targetLine] + Math.Min(col, maxCol);
                if (Shift)
                {
                    if (_selAnchor < 0) _selAnchor = _cursor;
                }
                else
                    _selAnchor = -1;
                _cursor = newPos;
                ResetBlink();
            }
        }
    }

    private void ResetBlink()
    {
        _cursorTimer = 0;
        _cursorVisible = true;
    }

    private int HitTestPosition(Vector2 mousePos)
    {
        UpdateLineCache();
        float textAreaX = _bubbleX + PaddingX;
        float textAreaY = _bubbleY + PaddingY;

        int lineIdx = (int)((mousePos.Y - textAreaY) / LineHeight);
        lineIdx = Math.Clamp(lineIdx, 0, _cachedLines.Count - 1);

        string line = _cachedLines[lineIdx];
        int lineStart = _lineStartIndices[lineIdx];

        int bestPos = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i <= line.Length; i++)
        {
            float x = textAreaX + FontManager.MeasureText(line[..i], FontSize);
            float dist = MathF.Abs(mousePos.X - x);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestPos = i;
            }
        }

        return lineStart + bestPos;
    }

    private void UpdateLineCache()
    {
        _cachedLines = WrapText(_text);
        _lineStartIndices.Clear();
        int idx = 0;
        foreach (var line in _cachedLines)
        {
            _lineStartIndices.Add(idx);
            idx += line.Length;
        }
    }

    private (int line, int col) GetLineCol(int pos)
    {
        for (int i = _lineStartIndices.Count - 1; i >= 0; i--)
        {
            if (pos >= _lineStartIndices[i])
                return (i, pos - _lineStartIndices[i]);
        }
        return (0, 0);
    }

    private List<string> WrapText(string text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add("");
            return lines;
        }

        string current = "";
        foreach (char c in text)
        {
            string test = current + c;
            if (FontManager.MeasureText(test, FontSize) > MaxLineWidth)
            {
                lines.Add(current);
                current = "" + c;
            }
            else
            {
                current = test;
            }
        }
        lines.Add(current);
        return lines;
    }

    public void Draw(Vector2 petPos, Vector2 petSize)
    {
        if (!Visible) return;

        UpdateLineCache();

        bool showPlaceholder = IsEditing && _text.Length == 0;
        var displayLines = showPlaceholder ? new List<string> { "type status..." } : _cachedLines;

        int maxW = 0;
        foreach (var line in displayLines)
            maxW = Math.Max(maxW, FontManager.MeasureText(line, FontSize));

        int cursorExtra = IsEditing ? 6 : 0;
        int bubbleWidth = Math.Max(maxW + PaddingX * 2 + CloseButtonSize + 4 + cursorExtra, 80);
        int bubbleHeight = displayLines.Count * LineHeight + PaddingY * 2 - 4;

        _bubbleX = petPos.X + (petSize.X - bubbleWidth) / 2f;
        _bubbleY = petPos.Y + petSize.Y * 0.55f - bubbleHeight;

        _bubbleRect = new Rectangle(_bubbleX, _bubbleY, bubbleWidth, bubbleHeight);
        _closeRect = new Rectangle(_bubbleX + bubbleWidth - CloseButtonSize - 4,
            _bubbleY + (bubbleHeight - CloseButtonSize) / 2f, CloseButtonSize, CloseButtonSize);

        var borderColor = IsEditing ? EditBorderColor : BorderColor;

        Raylib.DrawRectangleRounded(_bubbleRect, 0.3f, 4, BgColor);
        Raylib.DrawRectangleRoundedLines(_bubbleRect, 0.3f, 4, 1f, borderColor);

        float textAreaX = _bubbleX + PaddingX;
        float textAreaY = _bubbleY + PaddingY;

        // Draw selection highlight
        if (IsEditing && HasSelection)
        {
            int selMin = SelMin, selMax = SelMax;
            for (int i = 0; i < _cachedLines.Count; i++)
            {
                int lineStart = _lineStartIndices[i];
                int lineEnd = lineStart + _cachedLines[i].Length;

                if (selMax <= lineStart || selMin >= lineEnd) continue;

                int hlStart = Math.Max(selMin, lineStart) - lineStart;
                int hlEnd = Math.Min(selMax, lineEnd) - lineStart;

                float x1 = textAreaX + FontManager.MeasureText(_cachedLines[i][..hlStart], FontSize);
                float x2 = textAreaX + FontManager.MeasureText(_cachedLines[i][..hlEnd], FontSize);
                float y = textAreaY + i * LineHeight;

                Raylib.DrawRectangle((int)x1, (int)y, (int)(x2 - x1), FontSize, SelectionColor);
            }
        }

        // Draw text
        var textColor = showPlaceholder ? PlaceholderColor : TextColor;
        for (int i = 0; i < displayLines.Count; i++)
        {
            FontManager.DrawText(displayLines[i],
                (int)textAreaX,
                (int)(textAreaY + i * LineHeight),
                FontSize, textColor);
        }

        // Draw cursor
        if (IsEditing && _cursorVisible && !showPlaceholder)
        {
            var (cLine, cCol) = GetLineCol(_cursor);
            float cx = textAreaX + FontManager.MeasureText(_cachedLines[cLine][..cCol], FontSize);
            float cy = textAreaY + cLine * LineHeight;
            Raylib.DrawLine((int)cx + 1, (int)cy, (int)cx + 1, (int)cy + FontSize, CursorColor);
        }
        else if (IsEditing && _cursorVisible && showPlaceholder)
        {
            Raylib.DrawLine((int)textAreaX + 1, (int)textAreaY,
                (int)textAreaX + 1, (int)textAreaY + FontSize, CursorColor);
        }

        // Draw close button
        var closeCol = _closeHovered ? CloseHoverColor : CloseColor;
        float ccx = _closeRect.X + CloseButtonSize / 2f;
        float ccy = _closeRect.Y + CloseButtonSize / 2f;
        float half = 4f;
        Raylib.DrawLineEx(new Vector2(ccx - half, ccy - half), new Vector2(ccx + half, ccy + half), 2f, closeCol);
        Raylib.DrawLineEx(new Vector2(ccx + half, ccy - half), new Vector2(ccx - half, ccy + half), 2f, closeCol);
    }
}
