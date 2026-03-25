using System.Numerics;
using Raylib_cs;

namespace MouseHouse.UI;

public struct MenuItem
{
    public string Label;
    public int Id;
    public bool IsSeparator;
    public bool Enabled;

    public static MenuItem Item(string label, int id, bool enabled = true)
        => new() { Label = label, Id = id, Enabled = enabled };

    public static MenuItem Separator()
        => new() { IsSeparator = true };
}

/// <summary>
/// A right-click popup menu rendered on the transparent overlay.
/// </summary>
public class PopupMenu
{
    public bool Visible { get; private set; }
    public event Action<int>? OnItemSelected;

    private Vector2 _position;
    private readonly List<MenuItem> _items = new();
    private int _hoveredIndex = -1;

    // Styling
    private const int FontSize = 18;
    private const int ItemHeight = 28;
    private const int SeparatorHeight = 10;
    private const int PaddingX = 16;
    private const int PaddingY = 6;
    private const int MinWidth = 180;

    private static readonly Color BgColor = new(40, 40, 45, 240);
    private static readonly Color HoverColor = new(70, 130, 200, 200);
    private static readonly Color TextColor = new(230, 230, 230, 255);
    private static readonly Color DisabledColor = new(120, 120, 120, 255);
    private static readonly Color SepColor = new(80, 80, 85, 200);
    private static readonly Color BorderColor = new(60, 60, 65, 240);

    public void SetItems(List<MenuItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
    }

    public void Show(Vector2 position)
    {
        _position = position;
        Visible = true;
        _hoveredIndex = -1;

        // Clamp to screen bounds
        var size = GetMenuSize();
        var screenW = Raylib.GetScreenWidth();
        var screenH = Raylib.GetScreenHeight();
        if (_position.X + size.X > screenW)
            _position.X = screenW - size.X;
        if (_position.Y + size.Y > screenH)
            _position.Y = screenH - size.Y;
    }

    public void Hide()
    {
        Visible = false;
        _hoveredIndex = -1;
    }

    /// <summary>
    /// Returns true if the menu consumed the click (so caller shouldn't process it).
    /// </summary>
    public bool Update(Vector2 mousePos, bool leftPressed, bool rightPressed)
    {
        if (!Visible) return false;

        var size = GetMenuSize();
        var menuRect = new Rectangle(_position.X, _position.Y, size.X, size.Y);
        bool mouseInMenu = Raylib.CheckCollisionPointRec(mousePos, menuRect);

        // Find hovered item
        _hoveredIndex = -1;
        if (mouseInMenu)
        {
            float y = _position.Y + PaddingY;
            for (int i = 0; i < _items.Count; i++)
            {
                float itemH = _items[i].IsSeparator ? SeparatorHeight : ItemHeight;
                if (mousePos.Y >= y && mousePos.Y < y + itemH && !_items[i].IsSeparator)
                {
                    _hoveredIndex = i;
                    break;
                }
                y += itemH;
            }
        }

        // Click
        if (leftPressed)
        {
            if (mouseInMenu && _hoveredIndex >= 0 && _items[_hoveredIndex].Enabled)
            {
                OnItemSelected?.Invoke(_items[_hoveredIndex].Id);
                Hide();
                return true;
            }
            // Clicked outside menu -> close
            Hide();
            return mouseInMenu; // consume if was in menu area
        }

        // Right-click outside closes too
        if (rightPressed && !mouseInMenu)
        {
            Hide();
        }

        return mouseInMenu;
    }

    public void Draw()
    {
        if (!Visible) return;

        var size = GetMenuSize();

        // Background with border
        Raylib.DrawRectangleRounded(
            new Rectangle(_position.X, _position.Y, size.X, size.Y),
            0.05f, 4, BgColor
        );
        Raylib.DrawRectangleRoundedLines(
            new Rectangle(_position.X, _position.Y, size.X, size.Y),
            0.05f, 4, 1f, BorderColor
        );

        float y = _position.Y + PaddingY;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (item.IsSeparator)
            {
                float sepY = y + SeparatorHeight / 2f;
                Raylib.DrawLineEx(
                    new Vector2(_position.X + 8, sepY),
                    new Vector2(_position.X + size.X - 8, sepY),
                    1f, SepColor
                );
                y += SeparatorHeight;
                continue;
            }

            // Hover highlight
            if (i == _hoveredIndex && item.Enabled)
            {
                Raylib.DrawRectangleRounded(
                    new Rectangle(_position.X + 4, y, size.X - 8, ItemHeight),
                    0.15f, 4, HoverColor
                );
            }

            var textColor = item.Enabled ? TextColor : DisabledColor;
            Raylib.DrawText(item.Label, (int)(_position.X + PaddingX), (int)(y + 5), FontSize, textColor);
            y += ItemHeight;
        }
    }

    private Vector2 GetMenuSize()
    {
        float width = MinWidth;
        float height = PaddingY * 2;

        foreach (var item in _items)
        {
            if (item.IsSeparator)
            {
                height += SeparatorHeight;
            }
            else
            {
                var textW = Raylib.MeasureText(item.Label, FontSize);
                width = Math.Max(width, textW + PaddingX * 2);
                height += ItemHeight;
            }
        }

        return new Vector2(width, height);
    }

    /// <summary>
    /// Returns true if any part of the menu is under the given point.
    /// Useful for hit-testing (don't pass through clicks when menu is open).
    /// </summary>
    public bool ContainsPoint(Vector2 point)
    {
        if (!Visible) return false;
        var size = GetMenuSize();
        return Raylib.CheckCollisionPointRec(point,
            new Rectangle(_position.X, _position.Y, size.X, size.Y));
    }
}
