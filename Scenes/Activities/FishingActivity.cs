using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Rendering;

namespace MouseHouse.Scenes.Activities;

public class FishingActivity : IActivity
{
    public Vector2 PanelSize => new(800, 600);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly AudioManager _audio;

    // State
    private enum FishState { Idle, Casting, Waiting, Bite, Caught, Missed }
    private FishState _state = FishState.Idle;

    // Textures
    private Texture2D _bgTexture;
    private Texture2D _bobberTexture;

    // Bobber
    private Vector2 _bobberPos;
    private float _bobTime;

    // Fishing line
    private static readonly Vector2 LineStart = new(224, 484);

    // Timers
    private float _waitTimer;
    private float _biteTimer;
    private float _messageTimer;

    // Catch display
    private string _message = "";
    private Color _messageColor = Color.White;
    private string _catchName = "";
    private Texture2D? _catchTexture;
    private float _catchDisplayTimer;

    // Catch table: (name, weight, texturePath, isBig)
    private static readonly (string name, float weight, string path, bool big)[] CatchTable =
    {
        ("Small Fish", 20, "minnow", false),
        ("Seashell", 8, "seashell", false),
        ("Shiny Coin", 5, "coin", false),
        ("Rubber Duck", 5, "rubber_duck", false),
        ("Cork", 6, "cork", false),
        ("Driftwood", 5, "driftwood", false),
        ("Tin Can", 4, "tin_can", false),
        ("Old Sunglasses", 3, "sunglasses", false),
        ("Lost Sock", 3, "lost_sock", false),
        ("Bottle Cap", 2, "bottle_cap", false),
        ("Beach Ball", 4, "beach_ball", true),
        ("Message in a Bottle", 3, "message_bottle", true),
        ("Sailboat", 1.5f, "sailboat", true),
        ("Yacht", 0.5f, "yacht", true),
    };

    private static readonly Random Rng = new();

    public FishingActivity(AssetCache assets, AudioManager audio)
    {
        _assets = assets;
        _audio = audio;
    }

    public void Load()
    {
        _bgTexture = _assets.GetTexture("assets/fishing/fishing_bg.png");
        _bobberTexture = _assets.GetTexture("assets/fishing/bobber.png");
        _state = FishState.Idle;
        _message = "Click the water to cast!";
        _messageColor = Color.LightGray;
        _messageTimer = 999f;
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;

        _messageTimer -= delta;
        if (_messageTimer <= 0 && _state is FishState.Caught or FishState.Missed)
        {
            _state = FishState.Idle;
            _catchTexture = null;
            _message = "Click the water to cast!";
            _messageColor = Color.LightGray;
            _messageTimer = 999f;
        }

        switch (_state)
        {
            case FishState.Idle:
                if (leftPressed && local.Y > 60 && local.Y < 550 && local.X > 10 && local.X < 790)
                    Cast(local);
                break;

            case FishState.Waiting:
                _bobTime += delta;
                _bobberPos = _bobberPos with { Y = 80 + MathF.Sin(_bobTime * 3f) * 4f };
                _waitTimer -= delta;
                if (_waitTimer <= 0)
                    StartBite();
                break;

            case FishState.Bite:
                _bobTime += delta;
                _bobberPos = _bobberPos with { Y = 80 + MathF.Sin(_bobTime * 12f) * 12f };
                _biteTimer -= delta;
                if (leftPressed)
                    ReelIn();
                else if (_biteTimer <= 0)
                    Missed();
                break;
        }

        // Catch display fade
        if (_catchDisplayTimer > 0)
            _catchDisplayTimer -= delta;
    }

    private void Cast(Vector2 clickPos)
    {
        _state = FishState.Casting;
        _bobberPos = new Vector2(Math.Clamp(clickPos.X, 50, 750), 80);
        _waitTimer = Rng.NextSingle() * 4f + 2f; // 2-6 seconds
        _state = FishState.Waiting;
        _bobTime = 0;
        _message = "";
        _audio.Play("assets/audio/cast.wav");
    }

    private void StartBite()
    {
        _state = FishState.Bite;
        _biteTimer = 1.5f;
        _bobTime = 0;
        _message = "!";
        _messageColor = Color.Yellow;
        _messageTimer = 1.5f;
    }

    private void ReelIn()
    {
        _audio.Play("assets/audio/reel.wav");

        // Pick a catch
        float totalWeight = 0;
        foreach (var (_, w, _, _) in CatchTable)
            totalWeight += w;

        float roll = Rng.NextSingle() * totalWeight;
        float cumulative = 0;
        (string name, float weight, string path, bool big) caught = CatchTable[0];

        foreach (var entry in CatchTable)
        {
            cumulative += entry.weight;
            if (cumulative >= roll) { caught = entry; break; }
        }

        _catchName = caught.name;

        // Load catch texture
        if (caught.path == "minnow")
        {
            int idx = Rng.Next(16);
            var minnowPath = $"assets/fishing/minnow_{idx:D2}.png";
            try { _catchTexture = _assets.GetTexture(minnowPath); } catch { _catchTexture = null; }
        }
        else
        {
            var texPath = $"assets/fishing/{caught.path}.png";
            try { _catchTexture = _assets.GetTexture(texPath); } catch { _catchTexture = null; }
        }

        _state = FishState.Caught;
        _catchDisplayTimer = 2.5f;

        if (caught.big)
        {
            _message = $"Caught a {caught.name}!\nNice catch!";
            _messageColor = Color.Yellow;
            _messageTimer = 3f;
        }
        else
        {
            _message = $"Caught a {caught.name}!";
            _messageColor = Color.White;
            _messageTimer = 2f;
        }

        _audio.Play("assets/audio/catch_jingle.wav");
    }

    private void Missed()
    {
        _state = FishState.Missed;
        _message = "It got away!";
        _messageColor = new Color((byte)255, (byte)180, (byte)180, (byte)255);
        _messageTimer = 1.5f;
        _audio.Play("assets/audio/got_away.wav");
    }

    public void Draw(Vector2 offset)
    {
        // Background (200x150 → 800x600)
        var src = new Rectangle(0, 0, _bgTexture.Width, _bgTexture.Height);
        var dest = new Rectangle(offset.X, offset.Y, 800, 600);
        Raylib.DrawTexturePro(_bgTexture, src, dest, Vector2.Zero, 0f, Color.White);

        // Day/night overlay
        var overlay = TimeSystem.OverlayColor;
        if (overlay.A > 0)
            Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 600, overlay);

        // Fishing line + bobber when active
        if (_state is FishState.Waiting or FishState.Bite)
        {
            // Line
            var lineEnd = offset + _bobberPos + new Vector2(10, 10);
            Raylib.DrawLineEx(offset + LineStart, lineEnd, 2f,
                new Color((byte)180, (byte)180, (byte)180, (byte)150));

            // Bobber (5x8 at 4x scale = 20x32)
            var bobSrc = new Rectangle(0, 0, _bobberTexture.Width, _bobberTexture.Height);
            var bobDest = new Rectangle(offset.X + _bobberPos.X, offset.Y + _bobberPos.Y, 20, 32);
            Raylib.DrawTexturePro(_bobberTexture, bobSrc, bobDest, Vector2.Zero, 0f, Color.White);
        }

        // Caught item display
        if (_state == FishState.Caught && _catchTexture is { } tex && _catchDisplayTimer > 0)
        {
            float alpha = Math.Min(_catchDisplayTimer / 0.5f, 1f);
            var catchSrc = new Rectangle(0, 0, tex.Width, tex.Height);
            float scale = 4f;
            var catchDest = new Rectangle(
                offset.X + 400 - tex.Width * scale / 2,
                offset.Y + 250 - tex.Height * scale / 2,
                tex.Width * scale, tex.Height * scale);
            Raylib.DrawTexturePro(tex, catchSrc, catchDest, Vector2.Zero, 0f,
                new Color((byte)255, (byte)255, (byte)255, (byte)(alpha * 255)));
        }

        // Top bar
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 56,
            new Color((byte)30, (byte)30, (byte)35, (byte)180));
        Raylib.DrawText("Fishing", (int)offset.X + 10, (int)offset.Y + 18, 20, Color.White);
        Raylib.DrawText("[ESC] Exit", (int)offset.X + 700, (int)offset.Y + 18, 16, Color.LightGray);

        // Message
        if (_message != "" && _messageTimer > 0)
        {
            var lines = _message.Split('\n');
            int y = (int)offset.Y + 200;
            foreach (var line in lines)
            {
                int tw = Raylib.MeasureText(line, 24);
                Raylib.DrawText(line, (int)(offset.X + 400 - tw / 2), y, 24, _messageColor);
                y += 30;
            }
        }
    }

    public void Close()
    {
        IsFinished = true;
    }
}
