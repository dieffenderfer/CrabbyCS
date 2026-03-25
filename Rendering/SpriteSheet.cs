using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Rendering;

/// <summary>
/// A horizontal spritesheet with equally-sized frames.
/// </summary>
public class SpriteSheet
{
    public Texture2D Texture { get; }
    public int FrameCount { get; }
    public int FrameWidth { get; }
    public int FrameHeight { get; }

    public SpriteSheet(Texture2D texture, int frameCount)
    {
        Texture = texture;
        FrameCount = frameCount;
        FrameWidth = texture.Width / frameCount;
        FrameHeight = texture.Height;
    }

    /// <summary>
    /// Returns the source rectangle for the given frame index.
    /// </summary>
    public Rectangle GetFrameRect(int frame)
    {
        frame = frame % FrameCount;
        return new Rectangle(frame * FrameWidth, 0, FrameWidth, FrameHeight);
    }

    /// <summary>
    /// Draw a frame at the given position with optional flip and scale.
    /// </summary>
    public void DrawFrame(int frame, Vector2 position, float scale = 1f, bool flipH = false, Color? tint = null)
    {
        var src = GetFrameRect(frame);
        if (flipH)
            src.Width = -src.Width;

        var dest = new Rectangle(
            position.X, position.Y,
            FrameWidth * scale, FrameHeight * scale
        );

        Raylib.DrawTexturePro(Texture, src, dest, Vector2.Zero, 0f, tint ?? Color.White);
    }
}
