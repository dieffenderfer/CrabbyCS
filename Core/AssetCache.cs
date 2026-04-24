using Raylib_cs;
using MouseHouse.Rendering;

namespace MouseHouse.Core;

/// <summary>
/// Lazy-loading cache for textures, sounds, and spritesheets.
/// </summary>
public class AssetCache
{
    private readonly Dictionary<string, Texture2D> _textures = new();
    private readonly Dictionary<string, Sound> _sounds = new();
    private readonly string _basePath;
    public string BasePath => _basePath;

    public AssetCache(string basePath)
    {
        _basePath = basePath;
    }

    public Texture2D GetTexture(string relativePath)
    {
        if (_textures.TryGetValue(relativePath, out var tex))
            return tex;

        var fullPath = Path.Combine(_basePath, relativePath);
        tex = Raylib.LoadTexture(fullPath);
        // Use nearest-neighbor filtering for pixel art
        Raylib.SetTextureFilter(tex, TextureFilter.Point);
        _textures[relativePath] = tex;
        return tex;
    }

    public SpriteSheet GetSpriteSheet(string relativePath, int frameCount)
    {
        var tex = GetTexture(relativePath);
        return new SpriteSheet(tex, frameCount);
    }

    public SpriteSheet GetSpriteSheetWithAlpha(string relativePath, int frameCount)
    {
        var tex = GetTexture(relativePath);
        var fullPath = Path.Combine(_basePath, relativePath);
        var img = Raylib.LoadImage(fullPath);
        Raylib.ImageFormat(ref img, PixelFormat.UncompressedR8G8B8A8);
        var sheet = new SpriteSheet(tex, frameCount, img);
        Raylib.UnloadImage(img);
        return sheet;
    }

    public Sound GetSound(string relativePath)
    {
        if (_sounds.TryGetValue(relativePath, out var snd))
            return snd;

        var fullPath = Path.Combine(_basePath, relativePath);
        snd = Raylib.LoadSound(fullPath);
        _sounds[relativePath] = snd;
        return snd;
    }

    public void UnloadAll()
    {
        foreach (var tex in _textures.Values)
            Raylib.UnloadTexture(tex);
        _textures.Clear();

        foreach (var snd in _sounds.Values)
            Raylib.UnloadSound(snd);
        _sounds.Clear();
    }
}
