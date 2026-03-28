using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class DanceActivity : IActivity
{
    public Vector2 PanelSize => new(800, 600);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly AudioManager _audio;

    // Pads
    private static readonly string[] PadNames = { "Left", "Down", "Up", "Right" };
    private static readonly Color[] PadColors =
    {
        Color.Red, Color.Blue, Color.Green, Color.Yellow
    };
    private static readonly float[] PadX = { 200, 320, 440, 560 };
    private const float PadY = 500;
    private const float PadW = 80, PadH = 48;

    // Notes
    private readonly List<Note> _notes = new();
    private float _spawnTimer;
    private int _notesSent;
    private float _fallSpeed = 120f;
    private float _spawnInterval = 1.2f;

    // Score
    private int _score;
    private int _combo;
    private int _maxCombo;
    private int _totalNotes = 30;
    private bool _gameOver;
    private string _hitMessage = "";
    private float _hitMessageTimer;

    // Dance animation
    private int _danceFrame;
    private float _danceTimer;
    private bool _stumbling;
    private float _stumbleTimer;

    private static readonly Random Rng = new();

    public DanceActivity(AssetCache assets, AudioManager audio)
    {
        _assets = assets;
        _audio = audio;
    }

    public void Load()
    {
        _notesSent = 0;
        _score = 0;
        _combo = 0;
        _maxCombo = 0;
        _spawnTimer = 1f;
        _gameOver = false;
        _fallSpeed = 120f;
        _spawnInterval = 1.2f;
        _audio.Play("assets/audio/dance_music_loop.wav");
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        if (_gameOver) return;

        var local = mousePos - panelOffset;
        _hitMessageTimer -= delta;

        // Dance animation
        _danceTimer += delta;
        if (_danceTimer >= 0.3f)
        {
            _danceTimer -= 0.3f;
            _danceFrame = (_danceFrame + 1) % 4;
        }

        if (_stumbling)
        {
            _stumbleTimer -= delta;
            if (_stumbleTimer <= 0) _stumbling = false;
        }

        // Spawn notes
        if (_notesSent < _totalNotes)
        {
            _spawnTimer -= delta;
            if (_spawnTimer <= 0)
            {
                int pad = Rng.Next(4);
                _notes.Add(new Note { PadIndex = pad, Y = -20 });
                _notesSent++;
                _fallSpeed = 120f + _notesSent * 3f;
                _spawnInterval = Math.Max(0.5f, 1.2f - _notesSent * 0.02f);
                _spawnTimer = _spawnInterval;
            }
        }

        // Update notes
        for (int i = _notes.Count - 1; i >= 0; i--)
        {
            _notes[i].Y += _fallSpeed * delta;

            // Missed (fell past pad)
            if (_notes[i].Y > PadY + 60)
            {
                _combo = 0;
                _notes.RemoveAt(i);
            }
        }

        // Input: keyboard or mouse click on pads
        for (int p = 0; p < 4; p++)
        {
            bool padHit = false;

            // Keyboard
            if (p == 0 && (Raylib.IsKeyPressed(KeyboardKey.A) || Raylib.IsKeyPressed(KeyboardKey.Left))) padHit = true;
            if (p == 1 && (Raylib.IsKeyPressed(KeyboardKey.S) || Raylib.IsKeyPressed(KeyboardKey.Down))) padHit = true;
            if (p == 2 && (Raylib.IsKeyPressed(KeyboardKey.D) || Raylib.IsKeyPressed(KeyboardKey.Up))) padHit = true;
            if (p == 3 && (Raylib.IsKeyPressed(KeyboardKey.F) || Raylib.IsKeyPressed(KeyboardKey.Right))) padHit = true;

            // Mouse click on pad
            if (leftPressed && local.X >= PadX[p] - PadW / 2 && local.X < PadX[p] + PadW / 2
                && local.Y >= PadY - PadH / 2 && local.Y < PadY + PadH / 2)
                padHit = true;

            if (padHit)
                TryHitPad(p);
        }

        // Game over check
        if (_notesSent >= _totalNotes && _notes.Count == 0)
        {
            _gameOver = true;
            _audio.Play("assets/audio/score_reveal.wav");
        }
    }

    private void TryHitPad(int pad)
    {
        // Find closest note for this pad
        int bestIdx = -1;
        float bestDist = float.MaxValue;

        for (int i = 0; i < _notes.Count; i++)
        {
            if (_notes[i].PadIndex != pad) continue;
            float dist = MathF.Abs(_notes[i].Y - PadY);
            if (dist < 50 && dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        if (bestIdx >= 0)
        {
            _notes.RemoveAt(bestIdx);
            if (bestDist < 20)
            {
                _score += 20;
                _hitMessage = "Perfect!";
            }
            else
            {
                _score += 10;
                _hitMessage = "Good!";
            }
            _combo++;
            _maxCombo = Math.Max(_maxCombo, _combo);
            _hitMessageTimer = 0.5f;
            _audio.Play("assets/audio/hit_good.wav");
        }
        else
        {
            // Miss
            _combo = 0;
            _stumbling = true;
            _stumbleTimer = 0.5f;
            _hitMessage = "Miss!";
            _hitMessageTimer = 0.5f;
            _audio.Play("assets/audio/hit_miss.wav");
        }
    }

    public void Draw(Vector2 offset)
    {
        // Dark dance floor
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 600,
            new Color((byte)20, (byte)15, (byte)30, (byte)255));

        // Scrolling lane lines
        float time = (float)Raylib.GetTime();
        for (int p = 0; p < 4; p++)
        {
            float x = offset.X + PadX[p];
            Raylib.DrawLineEx(new Vector2(x, offset.Y), new Vector2(x, offset.Y + 600),
                1f, new Color((byte)50, (byte)50, (byte)70, (byte)255));
        }

        // Target line
        Raylib.DrawLineEx(
            new Vector2(offset.X + 160, offset.Y + PadY),
            new Vector2(offset.X + 640, offset.Y + PadY),
            2f, new Color((byte)100, (byte)100, (byte)120, (byte)255));

        // Falling notes
        foreach (var note in _notes)
        {
            float x = offset.X + PadX[note.PadIndex];
            float y = offset.Y + note.Y;
            Raylib.DrawRectangle((int)(x - 25), (int)(y - 12), 50, 24, PadColors[note.PadIndex]);
            Raylib.DrawRectangleLines((int)(x - 25), (int)(y - 12), 50, 24, Color.White);
        }

        // Pads
        for (int p = 0; p < 4; p++)
        {
            float x = offset.X + PadX[p];
            float y = offset.Y + PadY;
            var color = PadColors[p];
            Raylib.DrawRectangle((int)(x - PadW / 2), (int)(y - PadH / 2), (int)PadW, (int)PadH,
                new Color(color.R, color.G, color.B, (byte)100));
            Raylib.DrawRectangleLines((int)(x - PadW / 2), (int)(y - PadH / 2), (int)PadW, (int)PadH, color);
            Raylib.DrawText(PadNames[p], (int)(x - 15), (int)(y - 8), 16, Color.White);
        }

        // Dancing character (simple)
        float dancerX = offset.X + 80;
        float dancerY = offset.Y + 420;
        if (_stumbling)
        {
            Raylib.DrawCircleV(new Vector2(dancerX, dancerY), 20,
                new Color((byte)200, (byte)80, (byte)80, (byte)255));
            Raylib.DrawText("X_X", (int)dancerX - 12, (int)dancerY - 8, 16, Color.White);
        }
        else
        {
            float bounce = MathF.Sin(time * 8f) * 5f * (_danceFrame % 2 == 0 ? 1 : -1);
            Raylib.DrawCircleV(new Vector2(dancerX + bounce, dancerY - MathF.Abs(bounce)), 20,
                new Color((byte)100, (byte)200, (byte)255, (byte)255));
            Raylib.DrawText("^_^", (int)(dancerX + bounce - 12), (int)(dancerY - MathF.Abs(bounce) - 8), 16, Color.White);
        }

        // Top bar
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 44,
            new Color((byte)30, (byte)20, (byte)40, (byte)220));
        Raylib.DrawText("Dance!", (int)offset.X + 10, (int)offset.Y + 12, 20, Color.White);
        Raylib.DrawText($"Score: {_score}", (int)offset.X + 200, (int)offset.Y + 14, 18, Color.Yellow);
        Raylib.DrawText($"Combo: {_combo}", (int)offset.X + 380, (int)offset.Y + 14, 18, Color.Magenta);
        Raylib.DrawText("[ESC] Exit", (int)offset.X + 700, (int)offset.Y + 14, 14, Color.LightGray);
        Raylib.DrawText("[A] [S] [D] [F] or click pads", (int)offset.X + 250, (int)offset.Y + 560, 14, Color.LightGray);

        // Hit message
        if (_hitMessage != "" && _hitMessageTimer > 0)
        {
            int tw = Raylib.MeasureText(_hitMessage, 28);
            var color = _hitMessage == "Miss!" ? Color.Red :
                        _hitMessage == "Perfect!" ? Color.Gold : Color.Green;
            Raylib.DrawText(_hitMessage, (int)(offset.X + 400 - tw / 2), (int)offset.Y + 440, 28, color);
        }

        // Game over
        if (_gameOver)
        {
            Raylib.DrawRectangle((int)offset.X + 200, (int)offset.Y + 200, 400, 200,
                new Color((byte)20, (byte)20, (byte)40, (byte)230));
            Raylib.DrawText("Song Complete!", (int)offset.X + 280, (int)offset.Y + 230, 24, Color.Gold);
            Raylib.DrawText($"Score: {_score}", (int)offset.X + 320, (int)offset.Y + 270, 22, Color.White);
            Raylib.DrawText($"Max Combo: {_maxCombo}", (int)offset.X + 300, (int)offset.Y + 300, 20, Color.Magenta);
            Raylib.DrawText($"{_totalNotes} notes", (int)offset.X + 330, (int)offset.Y + 330, 18, Color.LightGray);
        }
    }

    public void Close() => IsFinished = true;

    private class Note
    {
        public int PadIndex;
        public float Y;
    }
}
