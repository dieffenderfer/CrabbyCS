using Raylib_cs;

namespace MouseHouse.Scenes.Activities.Chess;

/// <summary>
/// 16×16 pixel-art piece sprites, generated in-memory from packed bitmaps.
/// Used in place of Unicode chess glyphs (♔…♟) — Raylib's DrawTextEx renders
/// those as "?" since the bundled font's atlas doesn't include them, and the
/// pixel-art look matches the retro/Win9x aesthetic better anyway.
///
/// Each instance owns its textures; call <see cref="Unload"/> when the
/// activity closes. Pieces are indexed by signed piece value: positive=white,
/// negative=black, magnitudes 1=pawn 2=knight 3=bishop 4=rook 5=queen 6=king.
/// </summary>
public sealed class ChessPieceSprites
{
    // Each ushort is one row, bit (15-col) = pixel at column col.
    // Cross-king, spike-crown queen, crenellated rook, mitre+cleft bishop,
    // horse-head knight, round-headed pawn.
    private static readonly Dictionary<int, ushort[]> Bitmaps = new()
    {
        [6] = new ushort[] { 0x0000, 0x0180, 0x0180, 0x07E0, 0x07E0, 0x03C0, 0x07E0, 0x0FF0, 0x0FF0, 0x1FF8, 0x3FFC, 0x7FFE, 0x3FFC, 0x1FF8, 0x7FFE, 0x0000 },
        [5] = new ushort[] { 0x0000, 0x4992, 0x7FFE, 0x7FFE, 0x3FFC, 0x1FF8, 0x0FF0, 0x0FF0, 0x0FF0, 0x1FF8, 0x3FFC, 0x3FFC, 0x7FFE, 0x7FFE, 0xFFFF, 0x0000 },
        [4] = new ushort[] { 0x0000, 0x6666, 0x6666, 0x7FFE, 0x7FFE, 0x3FFC, 0x0FF0, 0x0FF0, 0x0FF0, 0x0FF0, 0x0FF0, 0x1FF8, 0x3FFC, 0x7FFE, 0xFFFF, 0x0000 },
        [3] = new ushort[] { 0x0000, 0x0180, 0x03C0, 0x03C0, 0x0660, 0x07E0, 0x0FF0, 0x0FF0, 0x07E0, 0x0FF0, 0x1FF8, 0x3FFC, 0x7FFE, 0x3FFC, 0xFFFF, 0x0000 },
        [2] = new ushort[] { 0x0000, 0x0FC0, 0x1FE0, 0x3FEC, 0x3FFC, 0x3FFC, 0x7BFF, 0x3FFC, 0x7FFE, 0x1FE0, 0x1FE0, 0x3FF0, 0x3FFC, 0x7FFE, 0xFFFF, 0x0000 },
        [1] = new ushort[] { 0x0000, 0x03C0, 0x07E0, 0x07E0, 0x07E0, 0x03C0, 0x03C0, 0x07E0, 0x0FF0, 0x0FF0, 0x1FF8, 0x3FFC, 0x7FFE, 0x7FFE, 0xFFFF, 0x0000 },
    };

    private const int Grid = 16;
    private const int PxScale = 2;            // 16×16 grid → 32×32 source texture
    private const int TexSize = Grid * PxScale;

    private static readonly Color WhiteFill = new(245, 240, 225, 255);
    private static readonly Color WhiteOutline = new(20, 15, 10, 255);
    private static readonly Color BlackFill = new(35, 25, 20, 255);
    private static readonly Color BlackOutline = new(180, 165, 145, 255);

    private readonly Dictionary<int, Texture2D> _textures = new();

    public void Load()
    {
        if (_textures.Count > 0) return;
        foreach (var (mag, bitmap) in Bitmaps)
        {
            _textures[mag] = Build(bitmap, white: true);
            _textures[-mag] = Build(bitmap, white: false);
        }
    }

    public void Unload()
    {
        foreach (var tex in _textures.Values) Raylib.UnloadTexture(tex);
        _textures.Clear();
    }

    public void Draw(int piece, int x, int y, int size)
    {
        if (piece == 0 || !_textures.TryGetValue(piece, out var tex)) return;
        var src = new Rectangle(0, 0, TexSize, TexSize);
        var dst = new Rectangle(x, y, size, size);
        Raylib.DrawTexturePro(tex, src, dst, System.Numerics.Vector2.Zero, 0, Color.White);
    }

    private static Texture2D Build(ushort[] bitmap, bool white)
    {
        var fill = white ? WhiteFill : BlackFill;
        var outline = white ? WhiteOutline : BlackOutline;

        var img = Raylib.GenImageColor(TexSize, TexSize, new Color(0, 0, 0, 0));

        bool Filled(int r, int c) =>
            r >= 0 && r < Grid && c >= 0 && c < Grid &&
            (bitmap[r] & (1 << (Grid - 1 - c))) != 0;

        void Block(int r, int c, Color color)
        {
            int x0 = c * PxScale, y0 = r * PxScale;
            for (int dy = 0; dy < PxScale; dy++)
                for (int dx = 0; dx < PxScale; dx++)
                    Raylib.ImageDrawPixel(ref img, x0 + dx, y0 + dy, color);
        }

        for (int r = 0; r < Grid; r++)
            for (int c = 0; c < Grid; c++)
            {
                if (!Filled(r, c)) continue;
                if (!Filled(r - 1, c)) Block(r - 1, c, outline);
                if (!Filled(r + 1, c)) Block(r + 1, c, outline);
                if (!Filled(r, c - 1)) Block(r, c - 1, outline);
                if (!Filled(r, c + 1)) Block(r, c + 1, outline);
            }
        for (int r = 0; r < Grid; r++)
            for (int c = 0; c < Grid; c++)
                if (Filled(r, c)) Block(r, c, fill);

        var tex = Raylib.LoadTextureFromImage(img);
        Raylib.SetTextureFilter(tex, TextureFilter.Point);
        Raylib.UnloadImage(img);
        return tex;
    }
}
