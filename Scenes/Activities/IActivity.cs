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
}
