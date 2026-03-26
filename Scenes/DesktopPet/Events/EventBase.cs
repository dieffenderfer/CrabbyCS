using System.Numerics;
using MouseHouse.Rendering;

namespace MouseHouse.Scenes.DesktopPet.Events;

/// <summary>
/// Base class for all random desktop events (seagulls, butterflies, rain, etc.)
/// Events are sprites that animate and move across the screen overlay.
/// </summary>
public abstract class EventBase
{
    public Vector2 Position;
    public bool Finished { get; protected set; }
    public float Lifetime { get; protected set; }
    public string Name { get; protected set; } = "";

    public SpriteSheet? Sheet;
    protected int Frame;
    protected float AnimTimer;
    protected float AnimSpeed = 0.15f;
    protected float Scale = 4f;
    protected bool FlipH;
    protected float Alpha = 1f;

    // Screen dimensions
    protected int ScreenW;
    protected int ScreenH;

    protected static readonly Random Rng = new();

    public virtual void Init(int screenW, int screenH)
    {
        ScreenW = screenW;
        ScreenH = screenH;
    }

    public virtual void Update(float delta)
    {
        Lifetime += delta;
        if (Lifetime > 120f) { Finished = true; return; }

        // Animate
        AnimTimer += delta;
        if (Sheet != null && AnimTimer >= AnimSpeed)
        {
            AnimTimer -= AnimSpeed;
            Frame = (Frame + 1) % Sheet.FrameCount;
        }
    }

    public virtual void Draw()
    {
        if (Sheet == null || Finished) return;

        var tint = new Raylib_cs.Color((byte)255, (byte)255, (byte)255, (byte)(Alpha * 255));
        Sheet.DrawFrame(Frame, Position, Scale, FlipH, tint);
    }
}
