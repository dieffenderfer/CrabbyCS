using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class SolitaireActivity : IActivity
{
    public Vector2 PanelSize => new(800, 600);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;

    // Card dimensions
    private const int CardW = 18, CardH = 24;
    private const float CardScale = 3f;
    private const float ScaledW = CardW * CardScale; // 54
    private const float ScaledH = CardH * CardScale; // 72
    private const float CascadeY = 25f;
    private const float MenuHeight = 28f;

    // Suits and values
    private static readonly string[] Suits = { "hearts", "diamonds", "clubs", "spades" };
    private static readonly string[] Values = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };

    // Game state
    private List<Card> _stock = new();
    private List<Card> _waste = new();
    private List<Card>[] _foundations = { new(), new(), new(), new() };
    private List<Card>[] _tableau = new List<Card>[7];
    private bool _won;

    // Textures
    private readonly Dictionary<string, Texture2D> _cardTextures = new();
    private Texture2D _cardBack;

    // Drag state
    private bool _dragging;
    private List<Card> _dragCards = new();
    private string _dragOrigin = "";
    private int _dragOriginIndex;
    private Vector2 _dragOffset;
    private List<Vector2> _dragStartPositions = new();

    // Double-click
    private float _lastClickTime;
    private Vector2 _lastClickPos;
    private const float DoubleClickTime = 0.4f;
    private const float DoubleClickDist = 10f;

    // Auto-complete
    private bool _autoCompleting;
    private float _autoCompleteTimer;
    private const float AutoCompleteDelay = 0.05f;

    // Layout positions (relative to panel)
    private Vector2 _stockPos;
    private Vector2 _wastePos;
    private Vector2[] _foundationPos = new Vector2[4];
    private float _tableauStartX;
    private float _tableauStartY;

    // Colors
    private static readonly Color BgColor = new(30, 90, 50, 255); // Green felt
    private static readonly Color EmptySlotColor = new(20, 70, 35, 255);
    private static readonly Color EmptySlotBorder = new(50, 120, 70, 255);

    private readonly Random _rng = new();

    public SolitaireActivity(AssetCache assets)
    {
        _assets = assets;
        for (int i = 0; i < 7; i++)
            _tableau[i] = new List<Card>();
    }

    public void Load()
    {
        // Load card textures
        foreach (var suit in Suits)
            foreach (var val in Values)
                _cardTextures[$"{val}_{suit}"] = _assets.GetTexture($"assets/cards/{val}_{suit}.png");

        _cardBack = _assets.GetTexture("assets/cards/backs/back_crab_pattern.png");

        // Calculate layout
        _stockPos = new Vector2(30, MenuHeight + 10);
        _wastePos = new Vector2(30 + ScaledW + 15, MenuHeight + 10);
        for (int i = 0; i < 4; i++)
            _foundationPos[i] = new Vector2(800 - (4 - i) * (ScaledW + 10) - 20, MenuHeight + 10);
        _tableauStartX = (800 - 7 * (ScaledW + 8)) / 2f;
        _tableauStartY = MenuHeight + 100;

        Deal();
    }

    private void Deal()
    {
        // Create and shuffle deck
        var deck = new List<Card>();
        foreach (var suit in Suits)
            foreach (var val in Values)
                deck.Add(new Card { Suit = suit, Value = val, FaceUp = false });

        // Fisher-Yates shuffle
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }

        // Clear state
        _stock.Clear(); _waste.Clear(); _won = false;
        _autoCompleting = false; _dragging = false;
        for (int i = 0; i < 4; i++) _foundations[i].Clear();
        for (int i = 0; i < 7; i++) _tableau[i].Clear();

        // Deal to tableau
        int idx = 0;
        for (int col = 0; col < 7; col++)
        {
            for (int row = 0; row <= col; row++)
            {
                var card = deck[idx++];
                card.FaceUp = row == col; // Only top card face up
                _tableau[col].Add(card);
            }
        }

        // Rest goes to stock
        for (int i = idx; i < deck.Count; i++)
        {
            deck[i].FaceUp = false;
            _stock.Add(deck[i]);
        }
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset, bool leftPressed, bool leftReleased, bool rightPressed)
    {
        // Convert mouse to panel-local coordinates
        var local = mousePos - panelOffset;

        if (_won) return;

        if (_autoCompleting)
        {
            _autoCompleteTimer -= delta;
            if (_autoCompleteTimer <= 0)
            {
                _autoCompleteTimer = AutoCompleteDelay;
                if (!DoAutoCompleteStep())
                {
                    _autoCompleting = false;
                    CheckWin();
                }
            }
            return;
        }

        if (leftPressed && !_dragging)
            OnMouseDown(local, delta);

        if (leftReleased && _dragging)
            OnMouseUp(local);

        if (_dragging)
            OnMouseMove(local);

        if (rightPressed)
            OnRightClick(local);

        // Check for auto-complete condition
        if (!_autoCompleting && _stock.Count == 0 && _waste.Count == 0)
        {
            bool allFaceUp = true;
            foreach (var col in _tableau)
                foreach (var c in col)
                    if (!c.FaceUp) { allFaceUp = false; break; }
            if (allFaceUp) _autoCompleting = true;
        }
    }

    private void OnMouseDown(Vector2 local, float delta)
    {
        float now = (float)Raylib.GetTime();
        bool isDoubleClick = (now - _lastClickTime < DoubleClickTime)
                          && Vector2.Distance(local, _lastClickPos) < DoubleClickDist;
        _lastClickTime = now;
        _lastClickPos = local;

        // Click stock
        if (HitTest(local, _stockPos))
        {
            if (_stock.Count > 0)
            {
                var card = _stock[^1];
                _stock.RemoveAt(_stock.Count - 1);
                card.FaceUp = true;
                _waste.Add(card);
            }
            else
            {
                // Recycle waste to stock
                _waste.Reverse();
                foreach (var c in _waste) c.FaceUp = false;
                _stock = new List<Card>(_waste);
                _waste.Clear();
            }
            return;
        }

        // Click/double-click waste
        if (_waste.Count > 0 && HitTest(local, _wastePos))
        {
            if (isDoubleClick)
            {
                TryAutoSendToFoundation(_waste, _waste.Count - 1, "waste");
                return;
            }
            StartDrag(new List<Card> { _waste[^1] }, "waste", _waste.Count - 1, local, _wastePos);
            return;
        }

        // Click tableau
        for (int col = 0; col < 7; col++)
        {
            var tableau = _tableau[col];
            if (tableau.Count == 0) continue;

            // Check from top card down
            for (int i = tableau.Count - 1; i >= 0; i--)
            {
                if (!tableau[i].FaceUp) continue;
                var cardPos = GetTableauCardPos(col, i);
                // For non-top cards, only the CASCADE_Y strip is clickable
                float clickH = (i == tableau.Count - 1) ? ScaledH : CascadeY;
                if (local.X >= cardPos.X && local.X <= cardPos.X + ScaledW
                    && local.Y >= cardPos.Y && local.Y <= cardPos.Y + clickH)
                {
                    if (isDoubleClick && i == tableau.Count - 1)
                    {
                        TryAutoSendToFoundation(tableau, i, $"tableau_{col}");
                        return;
                    }
                    var cards = tableau.GetRange(i, tableau.Count - i);
                    var positions = new List<Vector2>();
                    for (int j = 0; j < cards.Count; j++)
                        positions.Add(GetTableauCardPos(col, i + j));
                    StartDrag(cards, $"tableau_{col}", i, local, cardPos);
                    _dragStartPositions = positions;
                    return;
                }
            }
        }
    }

    private void OnMouseUp(Vector2 local)
    {
        if (!_dragging) return;

        var card = _dragCards[0];
        bool placed = false;

        // Try foundations (single card only)
        if (_dragCards.Count == 1)
        {
            for (int i = 0; i < 4; i++)
            {
                if (HitTest(local, _foundationPos[i]) && CanPlaceOnFoundation(card, i))
                {
                    RemoveDragFromOrigin();
                    _foundations[i].Add(card);
                    placed = true;
                    break;
                }
            }
        }

        // Try tableau columns
        if (!placed)
        {
            for (int col = 0; col < 7; col++)
            {
                var colPos = GetTableauDropRect(col);
                if (local.X >= colPos.X && local.X <= colPos.X + ScaledW
                    && local.Y >= colPos.Y && local.Y <= colPos.Y + colPos.W)
                {
                    if (CanPlaceOnTableau(card, col))
                    {
                        RemoveDragFromOrigin();
                        _tableau[col].AddRange(_dragCards);
                        placed = true;
                        break;
                    }
                }
            }
        }

        _dragging = false;
        _dragCards.Clear();

        if (placed)
        {
            AutoFlipTableau();
            CheckWin();
        }
    }

    private void OnMouseMove(Vector2 local)
    {
        // Positions updated in Draw based on drag state
    }

    private void OnRightClick(Vector2 local)
    {
        // Right-click waste -> auto-send
        if (_waste.Count > 0 && HitTest(local, _wastePos))
        {
            TryAutoSendToFoundation(_waste, _waste.Count - 1, "waste");
            return;
        }
        // Right-click tableau top card -> auto-send
        for (int col = 0; col < 7; col++)
        {
            var t = _tableau[col];
            if (t.Count > 0 && t[^1].FaceUp)
            {
                var pos = GetTableauCardPos(col, t.Count - 1);
                if (HitTest(local, pos))
                {
                    TryAutoSendToFoundation(t, t.Count - 1, $"tableau_{col}");
                    return;
                }
            }
        }
    }

    private void TryAutoSendToFoundation(List<Card> source, int index, string origin)
    {
        var card = source[index];
        for (int i = 0; i < 4; i++)
        {
            if (CanPlaceOnFoundation(card, i))
            {
                source.RemoveAt(index);
                _foundations[i].Add(card);
                AutoFlipTableau();
                CheckWin();
                return;
            }
        }
    }

    private bool CanPlaceOnFoundation(Card card, int foundIdx)
    {
        var pile = _foundations[foundIdx];
        if (pile.Count == 0)
            return card.Value == "A";
        var top = pile[^1];
        return top.Suit == card.Suit && ValueIndex(card.Value) == ValueIndex(top.Value) + 1;
    }

    private bool CanPlaceOnTableau(Card card, int col)
    {
        var pile = _tableau[col];
        if (pile.Count == 0)
            return card.Value == "K";
        var top = pile[^1];
        return IsRed(card) != IsRed(top) && ValueIndex(card.Value) == ValueIndex(top.Value) - 1;
    }

    private void StartDrag(List<Card> cards, string origin, int index, Vector2 mouseLocal, Vector2 cardPos)
    {
        _dragging = true;
        _dragCards = cards;
        _dragOrigin = origin;
        _dragOriginIndex = index;
        _dragOffset = mouseLocal - cardPos;
        _dragStartPositions = new List<Vector2>();
        for (int i = 0; i < cards.Count; i++)
            _dragStartPositions.Add(cardPos + new Vector2(0, i * CascadeY));
    }

    private void RemoveDragFromOrigin()
    {
        if (_dragOrigin == "waste")
        {
            _waste.RemoveAt(_waste.Count - 1);
        }
        else if (_dragOrigin.StartsWith("tableau_"))
        {
            int col = int.Parse(_dragOrigin[8..]);
            _tableau[col].RemoveRange(_dragOriginIndex, _dragCards.Count);
        }
    }

    private void AutoFlipTableau()
    {
        for (int col = 0; col < 7; col++)
        {
            var t = _tableau[col];
            if (t.Count > 0 && !t[^1].FaceUp)
                t[^1].FaceUp = true;
        }
    }

    private bool DoAutoCompleteStep()
    {
        // Find lowest card that can go to a foundation
        int bestVal = 99;
        int bestCol = -1;
        int bestFound = -1;

        for (int col = 0; col < 7; col++)
        {
            var t = _tableau[col];
            if (t.Count == 0) continue;
            var top = t[^1];
            for (int f = 0; f < 4; f++)
            {
                if (CanPlaceOnFoundation(top, f))
                {
                    int vi = ValueIndex(top.Value);
                    if (vi < bestVal) { bestVal = vi; bestCol = col; bestFound = f; }
                }
            }
        }

        if (bestCol < 0) return false;

        var card = _tableau[bestCol][^1];
        _tableau[bestCol].RemoveAt(_tableau[bestCol].Count - 1);
        _foundations[bestFound].Add(card);
        return true;
    }

    private void CheckWin()
    {
        int total = 0;
        foreach (var f in _foundations) total += f.Count;
        if (total == 52) _won = true;
    }

    public void Draw(Vector2 offset)
    {
        // Background
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 600, BgColor);

        // Menu bar
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, (int)MenuHeight, new Color(20, 60, 30, 255));
        Raylib.DrawText("Solitaire", (int)offset.X + 10, (int)offset.Y + 5, 18, Color.White);

        // Close button
        Raylib.DrawText("[X]", (int)offset.X + 760, (int)offset.Y + 5, 18, Color.White);

        // New Game button
        Raylib.DrawText("[New]", (int)offset.X + 700, (int)offset.Y + 5, 18, Color.White);

        // Empty slots
        DrawEmptySlot(offset + _stockPos);
        DrawEmptySlot(offset + _wastePos);
        for (int i = 0; i < 4; i++)
            DrawEmptySlot(offset + _foundationPos[i]);
        for (int col = 0; col < 7; col++)
            DrawEmptySlot(offset + new Vector2(_tableauStartX + col * (ScaledW + 8), _tableauStartY));

        // Stock pile (top card back)
        if (_stock.Count > 0)
            DrawCardBack(offset + _stockPos);

        // Waste pile (top card face)
        if (_waste.Count > 0)
            DrawCard(_waste[^1], offset + _wastePos);

        // Foundations
        for (int i = 0; i < 4; i++)
            if (_foundations[i].Count > 0)
                DrawCard(_foundations[i][^1], offset + _foundationPos[i]);

        // Tableau
        for (int col = 0; col < 7; col++)
        {
            for (int row = 0; row < _tableau[col].Count; row++)
            {
                var card = _tableau[col][row];
                // Skip cards being dragged
                if (_dragging && _dragCards.Contains(card)) continue;
                var pos = offset + GetTableauCardPos(col, row);
                if (card.FaceUp)
                    DrawCard(card, pos);
                else
                    DrawCardBack(pos);
            }
        }

        // Dragged cards
        if (_dragging)
        {
            var mouseLocal = Raylib.GetMousePosition() - offset;
            var basePos = mouseLocal - _dragOffset;
            for (int i = 0; i < _dragCards.Count; i++)
                DrawCard(_dragCards[i], offset + basePos + new Vector2(0, i * CascadeY));
        }

        // Win text
        if (_won)
        {
            Raylib.DrawText("YOU WIN!", (int)offset.X + 280, (int)offset.Y + 260, 42, Color.Gold);
        }
    }

    private void DrawCard(Card card, Vector2 pos)
    {
        var key = $"{card.Value}_{card.Suit}";
        if (_cardTextures.TryGetValue(key, out var tex))
        {
            var src = new Rectangle(0, 0, tex.Width, tex.Height);
            var dest = new Rectangle(pos.X, pos.Y, ScaledW, ScaledH);
            Raylib.DrawTexturePro(tex, src, dest, Vector2.Zero, 0f, Color.White);
        }
    }

    private void DrawCardBack(Vector2 pos)
    {
        var src = new Rectangle(0, 0, _cardBack.Width, _cardBack.Height);
        var dest = new Rectangle(pos.X, pos.Y, ScaledW, ScaledH);
        Raylib.DrawTexturePro(_cardBack, src, dest, Vector2.Zero, 0f, Color.White);
    }

    private void DrawEmptySlot(Vector2 pos)
    {
        Raylib.DrawRectangleRounded(
            new Rectangle(pos.X, pos.Y, ScaledW, ScaledH),
            0.08f, 4, EmptySlotColor);
        Raylib.DrawRectangleRoundedLines(
            new Rectangle(pos.X, pos.Y, ScaledW, ScaledH),
            0.08f, 4, 2f, EmptySlotBorder);
    }

    private Vector2 GetTableauCardPos(int col, int row)
    {
        return new Vector2(
            _tableauStartX + col * (ScaledW + 8),
            _tableauStartY + row * CascadeY
        );
    }

    private (float X, float Y, float W) GetTableauDropRect(int col)
    {
        float x = _tableauStartX + col * (ScaledW + 8);
        float y = _tableauStartY;
        float h = ScaledH;
        if (_tableau[col].Count > 0)
        {
            y += (_tableau[col].Count - 1) * CascadeY;
            h = ScaledH;
        }
        return (x, _tableauStartY, y - _tableauStartY + h);
    }

    private bool HitTest(Vector2 point, Vector2 pos)
    {
        return point.X >= pos.X && point.X <= pos.X + ScaledW
            && point.Y >= pos.Y && point.Y <= pos.Y + ScaledH;
    }

    private static int ValueIndex(string val) => Array.IndexOf(Values, val);
    private static bool IsRed(Card card) => card.Suit is "hearts" or "diamonds";

    public void Close()
    {
        IsFinished = true;
    }

    private class Card
    {
        public string Suit = "";
        public string Value = "";
        public bool FaceUp;
    }
}
