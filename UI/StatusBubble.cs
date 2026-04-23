using System.Numerics;
using Raylib_cs;

namespace MouseHouse.UI;

public class StatusBubble
{
    public bool Visible { get; private set; }
    public bool IsEditing { get; private set; }

    private string _text = "";
    private bool _cursorVisible;
    private float _cursorTimer;

    private const int FontSize = 14;
    private const int PaddingX = 10;
    private const int PaddingY = 6;
    private const int CloseButtonSize = 16;
    private const int MaxLength = 30;
    private const float CursorBlinkRate = 0.5f;

    private static readonly Color BgColor = new(40, 40, 45, 220);
    private static readonly Color BorderColor = new(60, 60, 65, 240);
    private static readonly Color TextColor = new(230, 230, 230, 255);
    private static readonly Color CloseColor = new(180, 80, 80, 255);
    private static readonly Color CloseHoverColor = new(220, 100, 100, 255);
    private static readonly Color CursorColor = new(200, 200, 200, 255);
    private static readonly Color EditBorderColor = new(70, 130, 200, 200);

    private Rectangle _bubbleRect;
    private Rectangle _closeRect;
    private bool _closeHovered;

    public void StartEditing()
    {
        Visible = true;
        IsEditing = true;
        _text = "";
        _cursorTimer = 0;
        _cursorVisible = true;
    }

    public void Hide()
    {
        Visible = false;
        IsEditing = false;
        _text = "";
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

        if (leftPressed)
        {
            if (_closeHovered)
            {
                Hide();
                return true;
            }

            if (Raylib.CheckCollisionPointRec(mousePos, _bubbleRect))
            {
                IsEditing = true;
                _cursorTimer = 0;
                _cursorVisible = true;
                return true;
            }

            if (IsEditing)
            {
                IsEditing = false;
                if (_text.Length == 0) Hide();
                return false;
            }
        }

        if (IsEditing)
        {
            _cursorTimer += delta;
            if (_cursorTimer >= CursorBlinkRate)
            {
                _cursorTimer -= CursorBlinkRate;
                _cursorVisible = !_cursorVisible;
            }

            int key = Raylib.GetCharPressed();
            while (key > 0)
            {
                if (key >= 32 && key <= 126 && _text.Length < MaxLength)
                    _text += (char)key;
                key = Raylib.GetCharPressed();
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && _text.Length > 0)
                _text = _text[..^1];

            if (Raylib.IsKeyDown(KeyboardKey.Backspace) && _text.Length > 0)
            {
                // Repeat delete is handled by Raylib's key repeat
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
            {
                IsEditing = false;
                if (_text.Length == 0) Hide();
                return true;
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Escape))
            {
                IsEditing = false;
                if (_text.Length == 0) Hide();
                return true;
            }
        }

        return false;
    }

    public void Draw(Vector2 petPos, Vector2 petSize)
    {
        if (!Visible) return;

        string displayText = _text;
        if (IsEditing && _text.Length == 0)
            displayText = "type status...";

        int textWidth = Raylib.MeasureText(displayText, FontSize);
        int bubbleWidth = Math.Max(textWidth + PaddingX * 2 + CloseButtonSize + 4, 80);
        int bubbleHeight = FontSize + PaddingY * 2;

        float bubbleX = petPos.X + (petSize.X - bubbleWidth) / 2f;
        float bubbleY = petPos.Y - bubbleHeight - 6;

        _bubbleRect = new Rectangle(bubbleX, bubbleY, bubbleWidth, bubbleHeight);
        _closeRect = new Rectangle(bubbleX + bubbleWidth - CloseButtonSize - 4, bubbleY + (bubbleHeight - CloseButtonSize) / 2f, CloseButtonSize, CloseButtonSize);

        var borderColor = IsEditing ? EditBorderColor : BorderColor;

        Raylib.DrawRectangleRounded(_bubbleRect, 0.3f, 4, BgColor);
        Raylib.DrawRectangleRoundedLines(_bubbleRect, 0.3f, 4, 1f, borderColor);

        var textColor = (_text.Length == 0 && IsEditing) ? new Color(150, 150, 150, 180) : TextColor;
        Raylib.DrawText(displayText, (int)(bubbleX + PaddingX), (int)(bubbleY + PaddingY), FontSize, textColor);

        if (IsEditing && _cursorVisible && _text.Length > 0)
        {
            int cursorX = (int)(bubbleX + PaddingX + Raylib.MeasureText(_text, FontSize));
            Raylib.DrawLine(cursorX + 1, (int)(bubbleY + PaddingY), cursorX + 1, (int)(bubbleY + PaddingY + FontSize), CursorColor);
        }

        var closeCol = _closeHovered ? CloseHoverColor : CloseColor;
        float cx = _closeRect.X + CloseButtonSize / 2f;
        float cy = _closeRect.Y + CloseButtonSize / 2f;
        float half = 4f;
        Raylib.DrawLineEx(new Vector2(cx - half, cy - half), new Vector2(cx + half, cy + half), 2f, closeCol);
        Raylib.DrawLineEx(new Vector2(cx + half, cy - half), new Vector2(cx - half, cy + half), 2f, closeCol);
    }
}
