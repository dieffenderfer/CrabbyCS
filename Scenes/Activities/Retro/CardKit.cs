using System.Numerics;
using Raylib_cs;

namespace MouseHouse.Scenes.Activities.Retro;

public enum Suit { Hearts, Diamonds, Clubs, Spades }

public class Card
{
    public Suit Suit;
    public int Rank;       // 1 = Ace .. 13 = King
    public bool FaceUp;

    public bool IsRed => Suit == Suit.Hearts || Suit == Suit.Diamonds;

    public string RankLabel => Rank switch
    {
        1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => Rank.ToString()
    };
}

/// <summary>
/// Shared playing-card primitives: deck construction, shuffle, procedural
/// rendering (no bitmap art so cards repaint correctly under any RetroTheme),
/// and hit-testing. Used by every Entertainment Pack solitaire variant.
/// </summary>
public static class CardKit
{
    public const int CardW = 50;
    public const int CardH = 72;
    public const int CascadeY = 18;     // spacing for face-up tableau fan
    public const int CascadeYDown = 5;  // spacing for face-down cards in fan

    private static readonly Color CardFace = new(255, 255, 255, 255);
    private static readonly Color CardBorder = new(0, 0, 0, 255);
    private static readonly Color RedSuit = new(208, 0, 0, 255);
    private static readonly Color BlackSuit = new(0, 0, 0, 255);
    private static readonly Color EmptySlot = new(0, 0, 0, 60);
    private static readonly Color BackPrimary = new(64, 32, 144, 255);
    private static readonly Color BackAccent = new(96, 64, 192, 255);

    public static List<Card> NewDeck()
    {
        var deck = new List<Card>(52);
        foreach (Suit s in Enum.GetValues(typeof(Suit)))
            for (int r = 1; r <= 13; r++)
                deck.Add(new Card { Suit = s, Rank = r, FaceUp = false });
        return deck;
    }

    public static void Shuffle(List<Card> deck, Random rng)
    {
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
    }

    public static Rectangle Rect(Vector2 pos) => new(pos.X, pos.Y, CardW, CardH);

    public static bool HitTest(Vector2 mouse, Vector2 pos)
        => RetroSkin.PointInRect(mouse, Rect(pos));

    public static void DrawEmptySlot(Vector2 pos)
    {
        var r = Rect(pos);
        Raylib.DrawRectangleRec(r, EmptySlot);
        // Dotted border
        for (int x = 0; x < CardW; x += 4)
        {
            Raylib.DrawPixel((int)pos.X + x, (int)pos.Y, RetroSkin.DarkShadow);
            Raylib.DrawPixel((int)pos.X + x, (int)pos.Y + CardH - 1, RetroSkin.DarkShadow);
        }
        for (int y = 0; y < CardH; y += 4)
        {
            Raylib.DrawPixel((int)pos.X, (int)pos.Y + y, RetroSkin.DarkShadow);
            Raylib.DrawPixel((int)pos.X + CardW - 1, (int)pos.Y + y, RetroSkin.DarkShadow);
        }
    }

    public static void DrawCardBack(Vector2 pos)
    {
        var r = Rect(pos);
        Raylib.DrawRectangleRec(r, CardFace);
        Raylib.DrawRectangleLines((int)pos.X, (int)pos.Y, CardW, CardH, CardBorder);
        // Inset solid back
        var inset = new Rectangle(pos.X + 3, pos.Y + 3, CardW - 6, CardH - 6);
        Raylib.DrawRectangleRec(inset, BackPrimary);
        // Diagonal weave pattern
        for (int y = 0; y < CardH - 6; y += 4)
            for (int x = 0; x < CardW - 6; x += 4)
            {
                if (((x / 4) + (y / 4)) % 2 == 0)
                    Raylib.DrawRectangle((int)pos.X + 3 + x, (int)pos.Y + 3 + y, 2, 2, BackAccent);
            }
        Raylib.DrawRectangleLines((int)inset.X, (int)inset.Y, (int)inset.Width, (int)inset.Height, RetroSkin.DarkShadow);
    }

    public static void DrawCard(Card c, Vector2 pos)
    {
        if (!c.FaceUp) { DrawCardBack(pos); return; }

        var r = Rect(pos);
        Raylib.DrawRectangleRec(r, CardFace);
        Raylib.DrawRectangleLines((int)pos.X, (int)pos.Y, CardW, CardH, CardBorder);

        var col = c.IsRed ? RedSuit : BlackSuit;

        // Top-left rank + pip
        RetroSkin.DrawText(c.RankLabel, (int)pos.X + 3, (int)pos.Y + 1, col, 16);
        DrawSuitPip(c.Suit, (int)pos.X + 6, (int)pos.Y + 18, 7, col);

        // Bottom-right rank + pip (mirrored ish — same orientation, just placed)
        int rTextW = RetroSkin.MeasureText(c.RankLabel, 16);
        RetroSkin.DrawText(c.RankLabel, (int)pos.X + CardW - rTextW - 3, (int)pos.Y + CardH - 18, col, 16);
        DrawSuitPip(c.Suit, (int)pos.X + CardW - 8, (int)pos.Y + CardH - 26, 7, col);

        // Center — for court cards, draw the letter big; otherwise the suit pip
        int cx = (int)pos.X + CardW / 2;
        int cy = (int)pos.Y + CardH / 2;
        if (c.Rank >= 11)
        {
            string label = c.RankLabel;
            int w = RetroSkin.MeasureText(label, 28);
            RetroSkin.DrawText(label, cx - w / 2, cy - 18, col, 28);
            DrawSuitPip(c.Suit, cx, cy + 14, 6, col);
        }
        else
        {
            DrawSuitPip(c.Suit, cx, cy, 12, col);
        }
    }

    /// <summary>Draws a suit pip centered at (cx, cy) with given half-size.</summary>
    public static void DrawSuitPip(Suit suit, int cx, int cy, int sz, Color col)
    {
        switch (suit)
        {
            case Suit.Hearts:
                // Two upper circles + bottom triangle
                Raylib.DrawCircle(cx - sz / 2, cy - sz / 4, sz / 2 + 1, col);
                Raylib.DrawCircle(cx + sz / 2, cy - sz / 4, sz / 2 + 1, col);
                Raylib.DrawTriangle(
                    new Vector2(cx - sz, cy),
                    new Vector2(cx, cy + sz),
                    new Vector2(cx + sz, cy),
                    col);
                break;
            case Suit.Diamonds:
                Raylib.DrawTriangle(
                    new Vector2(cx, cy - sz),
                    new Vector2(cx - sz * 3 / 4, cy),
                    new Vector2(cx, cy + sz),
                    col);
                Raylib.DrawTriangle(
                    new Vector2(cx, cy - sz),
                    new Vector2(cx, cy + sz),
                    new Vector2(cx + sz * 3 / 4, cy),
                    col);
                break;
            case Suit.Spades:
                // Inverted diamond head
                Raylib.DrawTriangle(
                    new Vector2(cx, cy - sz),
                    new Vector2(cx - sz, cy + sz / 4),
                    new Vector2(cx + sz, cy + sz / 4),
                    col);
                Raylib.DrawCircle(cx - sz / 2, cy + sz / 4, sz / 2 + 1, col);
                Raylib.DrawCircle(cx + sz / 2, cy + sz / 4, sz / 2 + 1, col);
                // Stem
                Raylib.DrawRectangle(cx - 1, cy + sz / 4, 2, sz / 2 + 1, col);
                Raylib.DrawTriangle(
                    new Vector2(cx - sz / 2 - 1, cy + sz),
                    new Vector2(cx + sz / 2 + 1, cy + sz),
                    new Vector2(cx, cy + sz / 4),
                    col);
                break;
            case Suit.Clubs:
                Raylib.DrawCircle(cx, cy - sz / 2, sz / 2 + 1, col);
                Raylib.DrawCircle(cx - sz / 2, cy + sz / 4, sz / 2 + 1, col);
                Raylib.DrawCircle(cx + sz / 2, cy + sz / 4, sz / 2 + 1, col);
                // Stem
                Raylib.DrawRectangle(cx - 1, cy, 2, sz / 2 + 1, col);
                Raylib.DrawTriangle(
                    new Vector2(cx - sz / 2 - 1, cy + sz),
                    new Vector2(cx + sz / 2 + 1, cy + sz),
                    new Vector2(cx, cy + sz / 4),
                    col);
                break;
        }
    }
}
