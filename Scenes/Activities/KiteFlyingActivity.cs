using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class KiteFlyingActivity : IActivity
{
    public Vector2 PanelSize => new(800, 600);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly AudioManager _audio;

    // Kite
    private Vector2 _kitePos = new(400, 200);
    private Vector2 _kiteVel;
    private static readonly Vector2 Anchor = new(400, 520);
    private const float TetherLength = 400f;
    private bool _crashed;
    private float _flightTime;
    private int _tricks;

    // Wind
    private float _windStrength = 25f;
    private float _windGust = 5f;
    private float _bobTime;
    private string _weather = "Sunny";
    private float _weatherTimer;

    // String segments
    private readonly Vector2[] _stringPoints = new Vector2[14];

    // Tail
    private readonly Vector2[] _tailPoints = new Vector2[8];

    // Clouds
    private readonly List<Cloud> _clouds = new();

    // Tug
    private int _rapidClicks;
    private float _rapidClickTimer;

    private static readonly Random Rng = new();

    private static readonly (string name, float wind, float gust)[] Weathers =
    {
        ("Sunny", 25, 5), ("Breezy", 50, 20), ("Cloudy", 15, 3),
        ("Light Rain", 35, 25), ("Windy", 80, 60), ("Golden Hour", 20, 5),
    };

    public KiteFlyingActivity(AssetCache assets, AudioManager audio)
    {
        _assets = assets;
        _audio = audio;
    }

    public void Load()
    {
        _kitePos = new Vector2(400, 200);
        _kiteVel = Vector2.Zero;
        _crashed = false;
        _flightTime = 0;
        _tricks = 0;
        _weatherTimer = Rng.NextSingle() * 20 + 15;
        ChangeWeather();

        // Init clouds
        for (int i = 0; i < 5; i++)
            _clouds.Add(new Cloud
            {
                Pos = new Vector2(Rng.NextSingle() * 900 - 50, Rng.NextSingle() * 200 + 30),
                Speed = Rng.NextSingle() * 20 + 10,
                Width = Rng.NextSingle() * 60 + 40,
            });

        // Init string
        for (int i = 0; i < _stringPoints.Length; i++)
            _stringPoints[i] = Vector2.Lerp(Anchor, _kitePos, i / (float)(_stringPoints.Length - 1));

        for (int i = 0; i < _tailPoints.Length; i++)
            _tailPoints[i] = _kitePos + new Vector2(0, i * 8);

        _audio.Play("assets/audio/wind_gentle.wav");
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        if (_crashed)
        {
            if (leftPressed)
            {
                _crashed = false;
                _kitePos = new Vector2(400, 200);
                _kiteVel = Vector2.Zero;
                _flightTime = 0;
            }
            return;
        }

        _flightTime += delta;
        _bobTime += delta;

        // Weather changes
        _weatherTimer -= delta;
        if (_weatherTimer <= 0)
        {
            ChangeWeather();
            _weatherTimer = Rng.NextSingle() * 20 + 15;
        }

        // Wind force
        var windForce = new Vector2(
            _windStrength + _windGust * MathF.Sin(_bobTime * 0.7f),
            MathF.Sin(_bobTime * 1.3f) * _windStrength * 0.3f
        );

        // Target: mouse position + wind, clamped
        var local = mousePos - panelOffset;
        var target = new Vector2(
            Math.Clamp(local.X + windForce.X * 0.5f, 80, 720),
            Math.Clamp(local.Y + windForce.Y * 0.3f - 50, 60, 480)
        );

        // Natural bob
        target += new Vector2(
            MathF.Sin(_bobTime * 2f) * 8f,
            MathF.Sin(_bobTime * 1.5f) * 15f
        );

        // Lerp toward target
        _kiteVel = Vector2.Lerp(_kiteVel, (target - _kitePos) * 1.5f, delta * 2f);
        _kitePos += _kiteVel * delta;

        // Tether constraint
        var toKite = _kitePos - Anchor;
        if (toKite.Length() > TetherLength)
            _kitePos = Anchor + Vector2.Normalize(toKite) * TetherLength;

        // Clamp
        _kitePos = new Vector2(
            Math.Clamp(_kitePos.X, 30, 770),
            Math.Clamp(_kitePos.Y, 30, 510)
        );

        // Rapid click trick detection
        _rapidClickTimer -= delta;
        if (leftPressed)
        {
            if (_rapidClickTimer > 0)
                _rapidClicks++;
            else
                _rapidClicks = 1;
            _rapidClickTimer = 0.5f;

            if (_rapidClicks >= 3)
            {
                _tricks++;
                _rapidClicks = 0;
                _audio.Play("assets/audio/kite_whoosh.wav");
            }
        }

        // Crash check
        if (_kitePos.Y > 510)
        {
            _crashed = true;
            return;
        }

        // Update string (Verlet-ish)
        _stringPoints[0] = Anchor;
        _stringPoints[^1] = _kitePos;
        for (int iter = 0; iter < 3; iter++)
        {
            for (int i = 1; i < _stringPoints.Length - 1; i++)
            {
                float t = i / (float)(_stringPoints.Length - 1);
                var ideal = Vector2.Lerp(Anchor, _kitePos, t);
                float sag = MathF.Sin(t * MathF.PI) * 20f;
                float sway = MathF.Sin(_bobTime * 1.5f + t * 3f) * _windStrength * 0.15f;
                _stringPoints[i] = new Vector2(ideal.X + sway, ideal.Y + sag);
            }
        }

        // Update tail
        _tailPoints[0] = _kitePos + new Vector2(0, 15);
        for (int i = 1; i < _tailPoints.Length; i++)
        {
            var target2 = _tailPoints[i - 1] + new Vector2(
                MathF.Sin(_bobTime * 3f + i * 0.8f) * 6f, 10f);
            _tailPoints[i] = Vector2.Lerp(_tailPoints[i], target2, delta * 8f);
        }

        // Update clouds
        foreach (var c in _clouds)
        {
            c.Pos = c.Pos with { X = c.Pos.X + c.Speed * delta };
            if (c.Pos.X > 850) c.Pos = c.Pos with { X = -80 };
        }
    }

    private void ChangeWeather()
    {
        var w = Weathers[Rng.Next(Weathers.Length)];
        _weather = w.name;
        _windStrength = w.wind;
        _windGust = w.gust;
    }

    public void Draw(Vector2 offset)
    {
        // Sky gradient
        for (int y = 0; y < 600; y += 4)
        {
            float t = y / 600f;
            byte r = (byte)(100 + t * 50);
            byte g = (byte)(160 + t * 40);
            byte b = (byte)(230 - t * 30);
            Raylib.DrawRectangle((int)offset.X, (int)offset.Y + y, 800, 4, new Color(r, g, b, (byte)255));
        }

        // Ground
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y + 530, 800, 70,
            new Color((byte)100, (byte)180, (byte)80, (byte)255));

        // Clouds
        foreach (var c in _clouds)
        {
            Raylib.DrawEllipse((int)(offset.X + c.Pos.X), (int)(offset.Y + c.Pos.Y),
                c.Width / 2, 15, new Color((byte)255, (byte)255, (byte)255, (byte)180));
        }

        // String
        for (int i = 0; i < _stringPoints.Length - 1; i++)
        {
            Raylib.DrawLineEx(offset + _stringPoints[i], offset + _stringPoints[i + 1],
                1.5f, new Color((byte)80, (byte)80, (byte)80, (byte)200));
        }

        // Tail
        for (int i = 0; i < _tailPoints.Length - 1; i++)
        {
            byte alpha = (byte)(200 - i * 22);
            Raylib.DrawLineEx(offset + _tailPoints[i], offset + _tailPoints[i + 1],
                2f, new Color((byte)255, (byte)80, (byte)80, alpha));
        }

        // Kite (diamond shape)
        var kp = offset + _kitePos;
        var kiteColor = new Color((byte)220, (byte)60, (byte)60, (byte)255);
        Raylib.DrawTriangle(
            new Vector2(kp.X, kp.Y - 20), new Vector2(kp.X - 15, kp.Y), new Vector2(kp.X + 15, kp.Y), kiteColor);
        Raylib.DrawTriangle(
            new Vector2(kp.X - 15, kp.Y), new Vector2(kp.X + 15, kp.Y), new Vector2(kp.X, kp.Y + 12),
            new Color((byte)180, (byte)40, (byte)40, (byte)255));

        // Anchor person
        Raylib.DrawCircleV(offset + Anchor - new Vector2(0, 15), 8,
            new Color((byte)200, (byte)160, (byte)120, (byte)255));
        Raylib.DrawLineEx(offset + Anchor - new Vector2(0, 7), offset + Anchor + new Vector2(0, 15),
            3f, new Color((byte)100, (byte)100, (byte)200, (byte)255));

        // Top bar
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 44,
            new Color((byte)40, (byte)60, (byte)80, (byte)200));
        Raylib.DrawText("Kite Flying", (int)offset.X + 10, (int)offset.Y + 12, 20, Color.White);
        Raylib.DrawText($"Weather: {_weather}", (int)offset.X + 200, (int)offset.Y + 14, 16, Color.LightGray);
        Raylib.DrawText($"Tricks: {_tricks}  Time: {_flightTime:F0}s",
            (int)offset.X + 450, (int)offset.Y + 14, 16, Color.Yellow);
        Raylib.DrawText("[ESC] Exit", (int)offset.X + 700, (int)offset.Y + 14, 14, Color.LightGray);

        // Crashed
        if (_crashed)
        {
            Raylib.DrawRectangle((int)offset.X + 250, (int)offset.Y + 250, 300, 100,
                new Color((byte)30, (byte)30, (byte)40, (byte)220));
            Raylib.DrawText("Crashed!", (int)offset.X + 340, (int)offset.Y + 270, 24, Color.Red);
            Raylib.DrawText("Click to fly again", (int)offset.X + 310, (int)offset.Y + 310, 16, Color.LightGray);
        }
    }

    public void Close() => IsFinished = true;

    private class Cloud
    {
        public Vector2 Pos;
        public float Speed, Width;
    }
}
