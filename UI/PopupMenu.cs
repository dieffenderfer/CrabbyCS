using System.Numerics;
using Raylib_cs;

namespace MouseHouse.UI;

public struct MenuItem
{
    public string Label;
    public int Id;
    public bool IsSeparator;
    public bool IsHeader;
    public bool Enabled;

    public static MenuItem Item(string label, int id, bool enabled = true)
        => new() { Label = label, Id = id, Enabled = enabled };

    public static MenuItem Separator()
        => new() { IsSeparator = true };

    public static MenuItem Header(string label)
        => new() { Label = label, IsHeader = true };
}

/// <summary>
/// A right-click popup menu rendered on the transparent overlay.
/// Uses section headers for visual grouping — all items are one click.
/// </summary>
public class PopupMenu
{
    public bool Visible { get; private set; }
    public event Action<int>? OnItemSelected;

    private Vector2 _position;
    private readonly List<MenuItem> _items = new();
    private int _hoveredIndex = -1;
    private float _scroll;
    private float _maxVisibleHeight;

    private const int FontSize = 18;
    private const int ItemHeight = 28;
    private const int HeaderHeight = 22;
    private const int SeparatorHeight = 10;
    private const int PaddingX = 16;
    private const int PaddingY = 6;
    private const int MinWidth = 180;
    private const float ScrollSpeed = 30f;

    private static readonly Color BgColor = new(40, 40, 45, 240);
    private static readonly Color HoverColor = new(70, 130, 200, 200);
    private static readonly Color TextColor = new(230, 230, 230, 255);
    private static readonly Color DisabledColor = new(120, 120, 120, 255);
    private static readonly Color HeaderColor = new(160, 180, 220, 255);
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
        _scroll = 0;

        var size = GetMenuSize();
        int monitorH = Raylib.GetMonitorHeight(Raylib.GetCurrentMonitor());
        if (monitorH <= 0) monitorH = 1200;

        _maxVisibleHeight = monitorH - 40;

        if (_position.X + size.X > Raylib.GetMonitorWidth(Raylib.GetCurrentMonitor()))
            _position.X -= size.X;
        if (_position.Y + Math.Min(size.Y, _maxVisibleHeight) > monitorH)
            _position.Y = monitorH - Math.Min(size.Y, _maxVisibleHeight);
        if (_position.X < 0) _position.X = 0;
        if (_position.Y < 0) _position.Y = 0;
    }

    public void Hide()
    {
        Visible = false;
        _hoveredIndex = -1;
    }

    public bool Update(Vector2 mousePos, bool leftPressed, bool rightPressed)
    {
        if (!Visible) return false;

        var size = GetMenuSize();
        float visH = Math.Min(size.Y, _maxVisibleHeight);
        var menuRect = new Rectangle(_position.X, _position.Y, size.X, visH);
        bool mouseInMenu = Raylib.CheckCollisionPointRec(mousePos, menuRect);

        if (mouseInMenu)
        {
            var wheel = Raylib.GetMouseWheelMove();
            _scroll -= wheel * ScrollSpeed;
            float maxScroll = Math.Max(0, size.Y - _maxVisibleHeight);
            _scroll = Math.Clamp(_scroll, 0, maxScroll);
        }

        _hoveredIndex = -1;
        if (mouseInMenu)
        {
            float y = _position.Y + PaddingY - _scroll;
            for (int i = 0; i < _items.Count; i++)
            {
                float itemH = _items[i].IsSeparator ? SeparatorHeight
                    : _items[i].IsHeader ? HeaderHeight
                    : ItemHeight;
                if (mousePos.Y >= y && mousePos.Y < y + itemH
                    && !_items[i].IsSeparator && !_items[i].IsHeader)
                {
                    _hoveredIndex = i;
                    break;
                }
                y += itemH;
            }
        }

        if (leftPressed)
        {
            if (mouseInMenu && _hoveredIndex >= 0 && _items[_hoveredIndex].Enabled)
            {
                OnItemSelected?.Invoke(_items[_hoveredIndex].Id);
                Hide();
                return true;
            }
            Hide();
            return mouseInMenu;
        }

        if (rightPressed && !mouseInMenu)
            Hide();

        return mouseInMenu;
    }

    public void Draw()
    {
        if (!Visible) return;

        var size = GetMenuSize();
        float visH = Math.Min(size.Y, _maxVisibleHeight);

        Raylib.DrawRectangleRounded(
            new Rectangle(_position.X, _position.Y, size.X, visH),
            0.03f, 4, BgColor
        );
        Raylib.DrawRectangleRoundedLines(
            new Rectangle(_position.X, _position.Y, size.X, visH),
            0.03f, 4, 1f, BorderColor
        );

        Raylib.BeginScissorMode(
            (int)_position.X, (int)_position.Y, (int)size.X, (int)visH);

        float y = _position.Y + PaddingY - _scroll;
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

            if (item.IsHeader)
            {
                Raylib.DrawText(item.Label, (int)(_position.X + PaddingX), (int)(y + 4), 12, HeaderColor);
                y += HeaderHeight;
                continue;
            }

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

        Raylib.EndScissorMode();
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
            else if (item.IsHeader)
            {
                var textW = Raylib.MeasureText(item.Label, 12);
                width = Math.Max(width, textW + PaddingX * 2);
                height += HeaderHeight;
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

    public bool ContainsPoint(Vector2 point)
    {
        if (!Visible) return false;
        var size = GetMenuSize();
        float visH = Math.Min(size.Y, _maxVisibleHeight);
        return Raylib.CheckCollisionPointRec(point,
            new Rectangle(_position.X, _position.Y, size.X, visH));
    }
}
