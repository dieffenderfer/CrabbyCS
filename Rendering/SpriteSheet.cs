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
    private readonly byte[]? _alpha;

    public SpriteSheet(Texture2D texture, int frameCount)
    {
        Texture = texture;
        FrameCount = frameCount;
        FrameWidth = texture.Width / frameCount;
        FrameHeight = texture.Height;
    }

    public SpriteSheet(Texture2D texture, int frameCount, Image image) : this(texture, frameCount)
    {
        int w = texture.Width, h = texture.Height;
        _alpha = new byte[w * h];
        unsafe
        {
            var pixels = (Color*)image.Data;
            for (int i = 0; i < w * h; i++)
                _alpha[i] = pixels[i].A;
        }
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

    /// <summary>
    /// Returns true if the screen-space point hits a non-transparent pixel of the given frame.
    /// </summary>
    public bool HitTest(int frame, Vector2 position, float scale, bool flipH, Vector2 point)
    {
        if (_alpha == null) return true;

        float displayW = FrameWidth * scale;
        float displayH = FrameHeight * scale;

        float localX = point.X - position.X;
        float localY = point.Y - position.Y;
        if (localX < 0 || localY < 0 || localX >= displayW || localY >= displayH)
            return false;

        int texX = (int)(localX / scale);
        int texY = (int)(localY / scale);
        if (texX >= FrameWidth) texX = FrameWidth - 1;
        if (texY >= FrameHeight) texY = FrameHeight - 1;

        if (flipH)
            texX = FrameWidth - 1 - texX;

        frame = frame % FrameCount;
        int imgX = frame * FrameWidth + texX;
        int idx = texY * Texture.Width + imgX;

        return idx >= 0 && idx < _alpha.Length && _alpha[idx] > 0;
    }
}
