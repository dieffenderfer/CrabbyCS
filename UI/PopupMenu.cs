using System.Numerics;
using Raylib_cs;

namespace MouseHouse.UI;

public struct MenuItem
{
    public string Label;
    public int Id;
    public bool IsSeparator;
    public bool Enabled;
    public List<MenuItem>? Children;

    public static MenuItem Item(string label, int id, bool enabled = true)
        => new() { Label = label, Id = id, Enabled = enabled };

    public static MenuItem Separator()
        => new() { IsSeparator = true };

    public static MenuItem Submenu(string label, List<MenuItem> children)
        => new() { Label = label, Id = -1, Enabled = true, Children = children };

    public bool HasSubmenu => Children != null && Children.Count > 0;
}

/// <summary>
/// A right-click popup menu rendered on the transparent overlay, with one level of submenus.
/// </summary>
public class PopupMenu
{
    public bool Visible { get; private set; }
    public event Action<int>? OnItemSelected;

    private Vector2 _position;
    private readonly List<MenuItem> _items = new();
    private int _hoveredIndex = -1;

    private PopupMenu? _submenu;
    private int _openSubmenuIndex = -1;

    private const int FontSize = 18;
    private const int ItemHeight = 28;
    private const int SeparatorHeight = 10;
    private const int PaddingX = 16;
    private const int PaddingY = 6;
    private const int MinWidth = 180;
    private const int SubmenuArrowPad = 20;

    private static readonly Color BgColor = new(40, 40, 45, 240);
    private static readonly Color HoverColor = new(70, 130, 200, 200);
    private static readonly Color TextColor = new(230, 230, 230, 255);
    private static readonly Color DisabledColor = new(120, 120, 120, 255);
    private static readonly Color SepColor = new(80, 80, 85, 200);
    private static readonly Color BorderColor = new(60, 60, 65, 240);
    private static readonly Color ArrowColor = new(180, 180, 180, 255);

    private static int MonitorW => Math.Max(Raylib.GetMonitorWidth(Raylib.GetCurrentMonitor()), 800);
    private static int MonitorH => Math.Max(Raylib.GetMonitorHeight(Raylib.GetCurrentMonitor()), 600);

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
        CloseSubmenu();

        var size = GetMenuSize();
        if (_position.X + size.X > MonitorW)
            _position.X = MonitorW - size.X;
        if (_position.Y + size.Y > MonitorH)
            _position.Y = MonitorH - size.Y;
    }

    public void Hide()
    {
        Visible = false;
        _hoveredIndex = -1;
        CloseSubmenu();
    }

    private void CloseSubmenu()
    {
        _submenu?.Hide();
        _submenu = null;
        _openSubmenuIndex = -1;
    }

    /// <summary>
    /// Returns true if the menu consumed the click (so caller shouldn't process it).
    /// </summary>
    public bool Update(Vector2 mousePos, bool leftPressed, bool rightPressed)
    {
        if (!Visible) return false;

        if (_submenu != null && _submenu.Visible)
        {
            bool subConsumed = _submenu.Update(mousePos, leftPressed, rightPressed);
            if (subConsumed) return true;
        }

        var size = GetMenuSize();
        var menuRect = new Rectangle(_position.X, _position.Y, size.X, size.Y);
        bool mouseInMenu = Raylib.CheckCollisionPointRec(mousePos, menuRect);

        int prevHovered = _hoveredIndex;
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

        if (_hoveredIndex != prevHovered && _hoveredIndex >= 0 && _items[_hoveredIndex].HasSubmenu)
        {
            OpenSubmenuAt(_hoveredIndex);
        }
        else if (_hoveredIndex != prevHovered && _hoveredIndex >= 0 && !_items[_hoveredIndex].HasSubmenu)
        {
            CloseSubmenu();
        }

        if (leftPressed)
        {
            if (mouseInMenu && _hoveredIndex >= 0 && _items[_hoveredIndex].Enabled)
            {
                if (_items[_hoveredIndex].HasSubmenu)
                {
                    OpenSubmenuAt(_hoveredIndex);
                    return true;
                }
                OnItemSelected?.Invoke(_items[_hoveredIndex].Id);
                Hide();
                return true;
            }
            if (!mouseInMenu && (_submenu == null || !_submenu.ContainsPoint(mousePos)))
            {
                Hide();
                return false;
            }
            return mouseInMenu;
        }

        if (rightPressed && !mouseInMenu && (_submenu == null || !_submenu.ContainsPoint(mousePos)))
        {
            Hide();
        }

        return mouseInMenu;
    }

    private void OpenSubmenuAt(int index)
    {
        if (_openSubmenuIndex == index) return;

        var item = _items[index];
        if (!item.HasSubmenu) return;

        CloseSubmenu();
        _openSubmenuIndex = index;

        _submenu = new PopupMenu();
        _submenu.OnItemSelected += (id) =>
        {
            OnItemSelected?.Invoke(id);
            Hide();
        };
        _submenu.SetItems(item.Children!);

        var size = GetMenuSize();
        float itemY = _position.Y + PaddingY;
        for (int i = 0; i < index; i++)
            itemY += _items[i].IsSeparator ? SeparatorHeight : ItemHeight;

        var subPos = new Vector2(_position.X + size.X - 4, itemY - PaddingY);

        var subSize = _submenu.GetMenuSize();
        if (subPos.X + subSize.X > MonitorW)
            subPos.X = _position.X - subSize.X + 4;
        if (subPos.Y + subSize.Y > MonitorH)
            subPos.Y = MonitorH - subSize.Y;

        _submenu._position = subPos;
        _submenu.Visible = true;
    }

    public void Draw()
    {
        if (!Visible) return;

        var size = GetMenuSize();

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

            if (i == _hoveredIndex && item.Enabled)
            {
                Raylib.DrawRectangleRounded(
                    new Rectangle(_position.X + 4, y, size.X - 8, ItemHeight),
                    0.15f, 4, HoverColor
                );
            }

            var textColor = item.Enabled ? TextColor : DisabledColor;
            Raylib.DrawText(item.Label, (int)(_position.X + PaddingX), (int)(y + 5), FontSize, textColor);

            if (item.HasSubmenu)
            {
                Raylib.DrawText("▸", (int)(_position.X + size.X - PaddingX - 4), (int)(y + 5), FontSize, ArrowColor);
            }

            y += ItemHeight;
        }

        _submenu?.Draw();
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
                float extra = item.HasSubmenu ? SubmenuArrowPad : 0;
                width = Math.Max(width, textW + PaddingX * 2 + extra);
                height += ItemHeight;
            }
        }

        return new Vector2(width, height);
    }

    /// <summary>
    /// Returns true if any part of the menu or its open submenu is under the given point.
    /// </summary>
    public bool ContainsPoint(Vector2 point)
    {
        if (!Visible) return false;
        var size = GetMenuSize();
        if (Raylib.CheckCollisionPointRec(point,
            new Rectangle(_position.X, _position.Y, size.X, size.Y)))
            return true;
        if (_submenu != null && _submenu.ContainsPoint(point))
            return true;
        return false;
    }
}
