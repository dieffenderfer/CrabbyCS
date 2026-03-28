using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class StargazingActivity : IActivity
{
    public Vector2 PanelSize => new(800, 600);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly AudioManager _audio;

    // Background stars
    private readonly List<BgStar> _bgStars = new();

    // Constellations
    private static readonly ConstellationDef[] Constellations =
    {
        new("Big Dipper", new Vector2[] {
            new(120,80), new(160,60), new(210,55), new(260,70),
            new(290,110), new(340,130), new(380,100)
        }),
        new("Little Dipper", new Vector2[] {
            new(480,60), new(500,90), new(530,110), new(560,100),
            new(580,80), new(610,95), new(630,70)
        }),
        new("Orion", new Vector2[] {
            new(200,200), new(220,240), new(240,280), new(220,320),
            new(200,360), new(260,280), new(300,280)
        }),
        new("Cassiopeia", new Vector2[] {
            new(500,200), new(530,240), new(560,220), new(590,260), new(620,240)
        }),
        new("Leo", new Vector2[] {
            new(100,420), new(140,400), new(180,410), new(210,440),
            new(170,460), new(130,450)
        }),
        new("Crab", new Vector2[] {
            new(400,380), new(430,360), new(460,370), new(480,400),
            new(460,430), new(430,420)
        }),
    };

    private readonly bool[] _discovered;
    private int _currentConstellation;
    private int _nextStar; // next star to click in sequence
    private readonly List<int> _clickedStars = new(); // indices within current constellation

    // Shooting star
    private Vector2 _shootingStarPos;
    private Vector2 _shootingStarVel;
    private bool _shootingStarActive;
    private float _shootingStarTimer;

    // Message
    private string _message = "";
    private float _messageTimer;
    private Color _messageColor = Color.White;

    private static readonly Random Rng = new();

    public StargazingActivity(AssetCache assets, AudioManager audio)
    {
        _assets = assets;
        _audio = audio;
        _discovered = new bool[Constellations.Length];
    }

    public void Load()
    {
        // Generate background stars (seeded for consistency)
        var starRng = new Random(12345);
        for (int i = 0; i < 80; i++)
        {
            _bgStars.Add(new BgStar
            {
                Pos = new Vector2(starRng.Next(800), starRng.Next(600)),
                Alpha = starRng.NextSingle() * 0.6f + 0.3f,
                TwinkleSpeed = starRng.NextSingle() * 1.5f + 1.5f,
                Phase = starRng.NextSingle() * MathF.PI * 2,
                Size = starRng.NextSingle() * 2f + 1f,
            });
        }

        _currentConstellation = 0;
        _nextStar = 0;
        _shootingStarTimer = Rng.NextSingle() * 10f + 5f;
        _message = "Click the stars to trace constellations!";
        _messageTimer = 3f;
        _messageColor = Color.LightGray;

        _audio.Play("assets/audio/night_ambient.wav");
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;
        _messageTimer -= delta;

        // Shooting star
        _shootingStarTimer -= delta;
        if (_shootingStarTimer <= 0 && !_shootingStarActive)
        {
            _shootingStarActive = true;
            _shootingStarPos = new Vector2(800 + 20, Rng.NextSingle() * 150 + 20);
            _shootingStarVel = new Vector2(-(Rng.NextSingle() * 200 + 200), Rng.NextSingle() * 100 + 100);
        }

        if (_shootingStarActive)
        {
            _shootingStarPos += _shootingStarVel * delta;
            if (_shootingStarPos.X < -100 || _shootingStarPos.Y > 650)
            {
                _shootingStarActive = false;
                _shootingStarTimer = Rng.NextSingle() * 17f + 8f;
            }

            // Click shooting star
            if (leftPressed && Vector2.Distance(local, _shootingStarPos) < 60)
            {
                _message = "You made a wish!";
                _messageColor = Color.Gold;
                _messageTimer = 2.5f;
                _shootingStarActive = false;
                _shootingStarTimer = Rng.NextSingle() * 17f + 8f;
                leftPressed = false; // Consume click
            }
        }

        if (!leftPressed) return;

        // Skip discovered constellations
        while (_currentConstellation < Constellations.Length && _discovered[_currentConstellation])
            _currentConstellation++;
        if (_currentConstellation >= Constellations.Length) return;

        var constellation = Constellations[_currentConstellation];

        // Check if clicked near the next star
        if (_nextStar < constellation.Stars.Length)
        {
            var starPos = constellation.Stars[_nextStar];
            if (Vector2.Distance(local, starPos) < 25)
            {
                _clickedStars.Add(_nextStar);
                _nextStar++;
                _audio.Play("assets/audio/star_connect.wav");

                if (_nextStar >= constellation.Stars.Length)
                {
                    // Constellation complete!
                    _discovered[_currentConstellation] = true;
                    _message = $"Discovered: {constellation.Name}!";
                    _messageColor = new Color((byte)150, (byte)200, (byte)255, (byte)255);
                    _messageTimer = 2.5f;
                    _audio.Play("assets/audio/constellation_complete.wav");

                    // Move to next
                    _currentConstellation++;
                    _nextStar = 0;
                    _clickedStars.Clear();

                    // Check if all done
                    while (_currentConstellation < Constellations.Length && _discovered[_currentConstellation])
                        _currentConstellation++;
                }
            }
            else
            {
                // Clicked wrong star — check if it's a different constellation star
                bool hitAnyStar = false;
                for (int i = 0; i < constellation.Stars.Length; i++)
                {
                    if (i != _nextStar && Vector2.Distance(local, constellation.Stars[i]) < 25)
                    {
                        hitAnyStar = true;
                        break;
                    }
                }
                if (hitAnyStar)
                {
                    // Reset sequence
                    _nextStar = 0;
                    _clickedStars.Clear();
                }
            }
        }
    }

    public void Draw(Vector2 offset)
    {
        // Dark sky background
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 600,
            new Color((byte)8, (byte)10, (byte)30, (byte)255));

        float time = (float)Raylib.GetTime();

        // Background stars (twinkle)
        foreach (var star in _bgStars)
        {
            float alpha = star.Alpha * (0.7f + 0.3f * MathF.Sin(time * star.TwinkleSpeed + star.Phase));
            Raylib.DrawCircleV(offset + star.Pos, star.Size,
                new Color((byte)255, (byte)255, (byte)230, (byte)(alpha * 255)));
        }

        // Draw discovered constellation lines
        for (int c = 0; c < Constellations.Length; c++)
        {
            if (!_discovered[c]) continue;
            var stars = Constellations[c].Stars;
            for (int i = 0; i < stars.Length - 1; i++)
            {
                Raylib.DrawLineEx(offset + stars[i], offset + stars[i + 1], 2f,
                    new Color((byte)100, (byte)150, (byte)255, (byte)180));
            }
            // Draw stars
            foreach (var s in stars)
                Raylib.DrawCircleV(offset + s, 4f, new Color((byte)200, (byte)220, (byte)255, (byte)255));

            // Label
            var center = stars[stars.Length / 2];
            Raylib.DrawText(Constellations[c].Name, (int)(offset.X + center.X - 20), (int)(offset.Y + center.Y - 20), 12,
                new Color((byte)150, (byte)180, (byte)255, (byte)150));
        }

        // Draw current constellation stars (undiscovered)
        if (_currentConstellation < Constellations.Length && !_discovered[_currentConstellation])
        {
            var constellation = Constellations[_currentConstellation];
            // Draw connected lines so far
            for (int i = 0; i < _clickedStars.Count - 1; i++)
            {
                Raylib.DrawLineEx(
                    offset + constellation.Stars[_clickedStars[i]],
                    offset + constellation.Stars[_clickedStars[i + 1]],
                    2f, new Color((byte)100, (byte)200, (byte)255, (byte)200));
            }

            // Draw clickable stars
            for (int i = 0; i < constellation.Stars.Length; i++)
            {
                bool isNext = i == _nextStar;
                bool isClicked = _clickedStars.Contains(i);
                float radius = isNext ? 5f + MathF.Sin(time * 4f) * 2f : 4f;
                var color = isClicked
                    ? new Color((byte)100, (byte)200, (byte)255, (byte)255)
                    : isNext
                        ? new Color((byte)255, (byte)255, (byte)150, (byte)255)
                        : new Color((byte)200, (byte)200, (byte)200, (byte)180);
                Raylib.DrawCircleV(offset + constellation.Stars[i], radius, color);
            }

            // Constellation name hint
            Raylib.DrawText($"Find: {constellation.Name}",
                (int)offset.X + 10, (int)offset.Y + 570, 18, Color.LightGray);
        }
        else if (_currentConstellation >= Constellations.Length)
        {
            Raylib.DrawText("All constellations discovered!",
                (int)offset.X + 250, (int)offset.Y + 570, 20, Color.Gold);
        }

        // Shooting star
        if (_shootingStarActive)
        {
            var ssPos = offset + _shootingStarPos;
            // Trail
            for (int i = 1; i <= 5; i++)
            {
                float t = i * 0.15f;
                var trailPos = ssPos - _shootingStarVel * t * 0.02f;
                byte alpha = (byte)(200 - i * 35);
                Raylib.DrawCircleV(trailPos, 3f - i * 0.4f,
                    new Color((byte)255, (byte)255, (byte)200, alpha));
            }
            Raylib.DrawCircleV(ssPos, 3f, new Color((byte)255, (byte)255, (byte)220, (byte)255));
        }

        // Top bar
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 40,
            new Color((byte)10, (byte)12, (byte)35, (byte)200));
        Raylib.DrawText("Stargazing", (int)offset.X + 10, (int)offset.Y + 10, 20, Color.White);
        Raylib.DrawText("[ESC] Exit", (int)offset.X + 700, (int)offset.Y + 10, 16, Color.LightGray);

        // Discovered count
        int found = _discovered.Count(d => d);
        Raylib.DrawText($"{found}/{Constellations.Length}", (int)offset.X + 150, (int)offset.Y + 12, 16, Color.LightGray);

        // Message
        if (_message != "" && _messageTimer > 0)
        {
            int tw = Raylib.MeasureText(_message, 24);
            Raylib.DrawText(_message, (int)(offset.X + 400 - tw / 2), (int)offset.Y + 280, 24, _messageColor);
        }
    }

    public void Close() => IsFinished = true;

    private class BgStar
    {
        public Vector2 Pos;
        public float Alpha, TwinkleSpeed, Phase, Size;
    }

    private record ConstellationDef(string Name, Vector2[] Stars);
}
