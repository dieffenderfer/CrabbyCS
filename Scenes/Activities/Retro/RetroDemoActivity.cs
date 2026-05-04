using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities;

namespace MouseHouse.Scenes.Activities.Retro;

/// <summary>
/// Smoke-test panel for the Stage 0 retro chrome kit. Renders the standard
/// frame, title bar, menu bar, status bar, plus sample widgets so we can eyeball
/// the look before any games are built on top.
/// </summary>
public class RetroDemoActivity : IActivity
{
    public Vector2 PanelSize => new(420, 300);
    public bool IsFinished { get; private set; }
    public bool UiScaled => true;

    private bool _checkA = true;
    private bool _checkB;
    private bool _btn1Armed, _btn2Armed, _checkAArmed, _checkBArmed;
    private int _clickCount;
    private string _status = "Ready";

    public void Load() { }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;

        // Title bar close button
        var titleBar = new Rectangle(0, 0, PanelSize.X, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        {
            IsFinished = true;
            return;
        }

        // Sample buttons
        var b1 = new Rectangle(20, PanelSize.Y - 60, 90, 24);
        var b2 = new Rectangle(120, PanelSize.Y - 60, 90, 24);
        if (RetroWidgets.ButtonHitTest(b1, local, leftPressed, leftReleased, ref _btn1Armed))
        {
            _clickCount++;
            _status = $"OK clicked {_clickCount} time(s)";
        }
        if (RetroWidgets.ButtonHitTest(b2, local, leftPressed, leftReleased, ref _btn2Armed))
        {
            _clickCount = 0;
            _status = "Reset";
        }

        // Sample checkboxes (state-only update; draw happens in Draw)
        _checkA = RetroWidgets.CheckboxHitTest(new Vector2(20, 80),
            "Show grid", _checkA, local, leftReleased, ref _checkAArmed);
        _checkB = RetroWidgets.CheckboxHitTest(new Vector2(20, 100),
            "Snap to pixels", _checkB, local, leftReleased, ref _checkBArmed);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        // Title bar (inset 3px)
        var title = new Rectangle(panelOffset.X + 3, panelOffset.Y + 3,
            PanelSize.X - 6, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(title, "Retro Chrome Demo", true);

        // Menu bar
        var menu = new Rectangle(panelOffset.X + 3,
            panelOffset.Y + 3 + RetroWidgets.TitleBarHeight,
            PanelSize.X - 6, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menu, new[] { "File", "Game", "Options", "Help" }, -1);

        // Body content
        float bodyY = panelOffset.Y + 3 + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 8;

        // Sunken sample area
        var sunken = new Rectangle(panelOffset.X + 20, bodyY, PanelSize.X - 40, 50);
        RetroSkin.DrawSunken(sunken);
        RetroSkin.DrawText("Sunken inset (text-field style)",
            (int)sunken.X + 6, (int)sunken.Y + 6, RetroSkin.BodyText);
        RetroSkin.DrawText($"Clicks: {_clickCount}",
            (int)sunken.X + 6, (int)sunken.Y + 24, RetroSkin.BodyText);

        // Checkboxes (visual)
        RetroWidgets.CheckboxVisual(new Vector2(panelOffset.X + 20, panelOffset.Y + 80),
            "Show grid", _checkA);
        RetroWidgets.CheckboxVisual(new Vector2(panelOffset.X + 20, panelOffset.Y + 100),
            "Snap to pixels", _checkB);

        // Group box
        var group = new Rectangle(panelOffset.X + 200, panelOffset.Y + 70,
            PanelSize.X - 220, 80);
        RetroWidgets.GroupBox(group, "Sample group");
        RetroSkin.DrawText("Bevel kit live.",
            (int)group.X + 12, (int)group.Y + 24, RetroSkin.BodyText);

        // Buttons (visual)
        var b1 = new Rectangle(panelOffset.X + 20, panelOffset.Y + PanelSize.Y - 60, 90, 24);
        var b2 = new Rectangle(panelOffset.X + 120, panelOffset.Y + PanelSize.Y - 60, 90, 24);
        RetroWidgets.ButtonVisual(b1, "OK", _btn1Armed);
        RetroWidgets.ButtonVisual(b2, "Reset", _btn2Armed);

        // Status bar
        var status = new Rectangle(panelOffset.X + 3,
            panelOffset.Y + PanelSize.Y - 3 - RetroWidgets.StatusBarHeight,
            PanelSize.X - 6, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _status, "Stage 0");
    }

    public void Close() { }
}
