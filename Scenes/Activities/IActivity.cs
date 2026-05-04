using System.Numerics;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Interface for activities that render as opaque panels on the overlay.
/// </summary>
public interface IActivity
{
    /// <summary>Panel width/height in pixels.</summary>
    Vector2 PanelSize { get; }

    void Load();
    void Update(float delta, Vector2 mousePos, Vector2 panelOffset, bool leftPressed, bool leftReleased, bool rightPressed);
    void Draw(Vector2 panelOffset);
    void Close();

    bool IsFinished { get; }

    /// <summary>If true, DesktopPetScene skips the drop-shadow chrome — for zones with floating props.</summary>
    bool TransparentBackground => false;

    /// <summary>
    /// If true, the activity is authored at 1x logical pixels and the scene
    /// renders it through a render texture scaled by RetroSkin.UiScale.
    /// Mouse coords passed to Update are reverse-scaled into logical space.
    /// </summary>
    bool UiScaled => false;

    /// <summary>
    /// Returns true if a click at this panel-local point should be consumed by the activity
    /// (blocking pet input). Default: anywhere in the panel rect. Zones override to leave
    /// empty space click-through, so the pet can be dragged/dropped over the zone.
    /// </summary>
    bool ContainsPoint(Vector2 panelLocalPos) =>
        panelLocalPos.X >= 0 && panelLocalPos.Y >= 0 &&
        panelLocalPos.X <= PanelSize.X && panelLocalPos.Y <= PanelSize.Y;

    /// <summary>
    /// Called when a click lands within the title-bar rect, BEFORE DesktopPetScene starts
    /// the activity drag. Return true to indicate the activity handled the click (e.g. a
    /// button drawn inside the title bar) so the drag does not start.
    /// </summary>
    bool OnTitleBarClick(Vector2 panelLocalPos) => false;
}
