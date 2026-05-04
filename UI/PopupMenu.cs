using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;
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
    private const int SubmenuGap = 4;

    // Colors are themed: pulled live from RetroSkin so the popup follows the
    // current Retro Theme. No alpha — the menu is a fully opaque Win9x widget.

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

    private bool IsInMenuOrSubmenu(Vector2 point)
    {
        var size = GetMenuSize();
        if (Raylib.CheckCollisionPointRec(point,
            new Rectangle(_position.X, _position.Y, size.X, size.Y)))
            return true;
        if (_submenu != null && _submenu.Visible)
        {
            if (_submenu.ContainsPoint(point))
                return true;
            // Also include the gap between parent and submenu
            var subSize = _submenu.GetMenuSize();
            float gapLeft = Math.Min(_position.X + size.X, _submenu._position.X);
            float gapRight = Math.Max(_position.X + size.X, _submenu._position.X);
            float gapTop = Math.Min(_position.Y, _submenu._position.Y);
            float gapBottom = Math.Max(_position.Y + size.Y, _submenu._position.Y + subSize.Y);
            if (point.X >= gapLeft && point.X <= gapRight
                && point.Y >= gapTop && point.Y <= gapBottom)
                return true;
        }
        return false;
    }

    public bool Update(Vector2 mousePos, bool leftPressed, bool rightPressed)
    {
        if (!Visible) return false;

        bool mouseAnywhere = IsInMenuOrSubmenu(mousePos);
        var size = GetMenuSize();
        var menuRect = new Rectangle(_position.X, _position.Y, size.X, size.Y);
        bool mouseInMenu = Raylib.CheckCollisionPointRec(mousePos, menuRect);
        bool mouseInSub = _submenu != null && _submenu.Visible && _submenu.ContainsPoint(mousePos);

        // Always update parent hover tracking (even when submenu is open)
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

        // Open/close submenus on hover changes
        if (_hoveredIndex != prevHovered && _hoveredIndex >= 0 && _items[_hoveredIndex].HasSubmenu)
        {
            OpenSubmenuAt(_hoveredIndex);
        }
        else if (_hoveredIndex != prevHovered && _hoveredIndex >= 0 && !_items[_hoveredIndex].HasSubmenu)
        {
            CloseSubmenu();
        }

        // Let submenu handle clicks if mouse is over it
        if (_submenu != null && _submenu.Visible)
        {
            bool subConsumed = _submenu.Update(mousePos, leftPressed, rightPressed);
            if (subConsumed) return true;
        }

        if (leftPressed)
        {
            if (mouseInMenu && _hoveredIndex >= 0 && _items[_hoveredIndex].Enabled
                && !_items[_hoveredIndex].HasSubmenu)
            {
                OnItemSelected?.Invoke(_items[_hoveredIndex].Id);
                Hide();
                return true;
            }
            if (mouseAnywhere)
                return true;
            Hide();
            return false;
        }

        if (rightPressed && !mouseAnywhere)
            Hide();

        return mouseAnywhere;
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

        var subPos = new Vector2(_position.X + size.X - SubmenuGap, itemY - PaddingY);

        var subSize = _submenu.GetMenuSize();
        if (subPos.X + subSize.X > MonitorW)
            subPos.X = _position.X - subSize.X + SubmenuGap;
        if (subPos.Y + subSize.Y > MonitorH)
            subPos.Y = MonitorH - subSize.Y;

        _submenu._position = subPos;
        _submenu.Visible = true;
    }

    public void Draw()
    {
        if (!Visible) return;

        var size = GetMenuSize();
        var rect = new Rectangle(_position.X, _position.Y, size.X, size.Y);

        // Raised Win9x menu frame, fully opaque, themed.
        RetroSkin.DrawRaised(rect);

        float y = _position.Y + PaddingY;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (item.IsSeparator)
            {
                int sepY = (int)(y + SeparatorHeight / 2f);
                Raylib.DrawRectangle((int)_position.X + 4, sepY,
                    (int)size.X - 8, 1, RetroSkin.Shadow);
                Raylib.DrawRectangle((int)_position.X + 4, sepY + 1,
                    (int)size.X - 8, 1, RetroSkin.Highlight);
                y += SeparatorHeight;
                continue;
            }

            bool hovered = i == _hoveredIndex && item.Enabled;
            if (hovered)
            {
                Raylib.DrawRectangle((int)_position.X + 3, (int)y,
                    (int)size.X - 6, ItemHeight, RetroSkin.TitleActive);
            }

            var textColor = !item.Enabled ? RetroSkin.DisabledText
                          : hovered ? RetroSkin.TitleText
                          : RetroSkin.BodyText;
            FontManager.DrawText(item.Label, (int)(_position.X + PaddingX), (int)(y + 5), FontSize, textColor);

            if (item.HasSubmenu)
            {
                FontManager.DrawText("▸", (int)(_position.X + size.X - PaddingX - 4),
                    (int)(y + 5), FontSize, textColor);
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
                var textW = FontManager.MeasureText(item.Label, FontSize);
                float extra = item.HasSubmenu ? SubmenuArrowPad : 0;
                width = Math.Max(width, textW + PaddingX * 2 + extra);
                height += ItemHeight;
            }
        }

        return new Vector2(width, height);
    }

    public bool ContainsPoint(Vector2 point)
    {
        if (!Visible) return false;
        return IsInMenuOrSubmenu(point);
    }
}
