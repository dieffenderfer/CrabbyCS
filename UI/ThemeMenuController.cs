using System.Numerics;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.UI;

/// <summary>
/// Reusable right-click "Retro Theme" picker. Wraps a PopupMenu, owns the
/// hover-preview / revert-on-cancel state, and surfaces a Committed callback
/// so the host can persist + broadcast (ThemeSync) when the user actually
/// picks a theme. Same UX shape as the pet's context-menu submenu, lifted
/// out so the radio + golf widgets can mount it on their own title bars.
/// </summary>
public class ThemeMenuController
{
    private readonly PopupMenu _menu = new();
    private string? _committedThemeName;

    /// <summary>Fires with the chosen theme name when the user clicks an item.</summary>
    public Action<string>? Committed;

    public bool Visible => _menu.Visible;

    public ThemeMenuController()
    {
        _menu.OnItemSelected += OnSelected;
        _menu.OnItemHover += OnHover;
    }

    public void Show(Vector2 atScreenPos)
    {
        var items = new List<MenuItem>();
        for (int i = 0; i < RetroSkin.AllThemes.Length; i++)
        {
            var t = RetroSkin.AllThemes[i];
            // Dim the active theme so the user sees which one is current.
            items.Add(MenuItem.Item(t.Name, i, RetroSkin.Current.Name != t.Name));
        }
        _menu.SetItems(items);
        _menu.Show(atScreenPos);
        _committedThemeName = null;
    }

    public void Hide() => _menu.Hide();

    /// <summary>
    /// Run the menu's input handling. Returns true if the menu consumed
    /// the click (so the host should suppress its own click logic).
    /// </summary>
    public bool Update(Vector2 mouse, bool leftPressed, bool rightPressed)
        => _menu.Update(mouse, leftPressed, rightPressed);

    public void Draw() => _menu.Draw();

    public bool ContainsPoint(Vector2 p) => _menu.ContainsPoint(p);

    private void OnHover(int id)
    {
        // Live-preview the hovered theme; revert to the committed baseline
        // when the cursor leaves the theme list (id == -1 or on a divider).
        if (id >= 0 && id < RetroSkin.AllThemes.Length)
        {
            _committedThemeName ??= RetroSkin.Current.Name;
            RetroSkin.Current = RetroSkin.AllThemes[id];
        }
        else if (_committedThemeName != null)
        {
            foreach (var t in RetroSkin.AllThemes)
                if (t.Name == _committedThemeName) { RetroSkin.Current = t; break; }
            if (id == -1) _committedThemeName = null;
        }
    }

    private void OnSelected(int id)
    {
        if (id < 0 || id >= RetroSkin.AllThemes.Length) return;
        RetroSkin.Current = RetroSkin.AllThemes[id];
        // Update the preview baseline so the trailing OnHover(-1) (which
        // fires when the menu hides itself after the click) doesn't bounce
        // us back to the old theme.
        _committedThemeName = RetroSkin.Current.Name;
        Committed?.Invoke(RetroSkin.Current.Name);
    }
}
