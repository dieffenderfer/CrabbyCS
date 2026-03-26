using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Rendering;
using MouseHouse.Scenes.Activities;

namespace MouseHouse.Scenes.BeachHub;

/// <summary>
/// Beach hub scene: 800x600 pixel-art beach with click-to-move player,
/// interactive zones, and activity launching.
/// </summary>
public class BeachHubScene : IActivity
{
    public Vector2 PanelSize => new(800, 600);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly AudioManager _audio;

    // Background
    private Texture2D _bgTexture;

    // Player
    private SpriteSheet _playerSheet = null!;
    private Vector2 _playerPos = new(400, 450);
    private Vector2? _moveTarget;
    private float _playerSpeed = 120f;
    private int _playerFrame;
    private float _playerAnimTimer;
    private bool _playerFlipH;
    private float _footstepTimer;

    // Walkable bounds (polygon corners)
    private static readonly Vector2[] WalkablePolygon = {
        new(20, 260), new(780, 260), new(780, 555), new(20, 555)
    };

    // Zones
    private static readonly Rectangle OceanZone = new(0, 0, 800, 240);
    private static readonly Rectangle SandcastleZone = new(608, 288, 128, 152);
    private static readonly Rectangle FarmZone = new(20, 350, 140, 100);

    // Hover highlights
    private float _oceanHighlight, _sandcastleHighlight, _farmHighlight;
    private string _hoveredZone = "";

    // Activities
    private IActivity? _activeSubActivity;
    private Vector2 _subActivityOffset = Vector2.Zero;

    public BeachHubScene(AssetCache assets, AudioManager audio)
    {
        _assets = assets;
        _audio = audio;
    }

    public void Load()
    {
        _bgTexture = _assets.GetTexture("assets/sprites/beach_bg.png");
        _playerSheet = _assets.GetSpriteSheet("assets/sprites/mouse_char.png", 3);
        // mouse_char: 48x16, 3 frames of 16x16
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;

        // If sub-activity is open, delegate
        if (_activeSubActivity != null)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Escape))
            {
                CloseSubActivity();
                return;
            }
            _activeSubActivity.Update(delta, mousePos, _subActivityOffset,
                leftPressed, leftReleased, rightPressed);
            if (_activeSubActivity.IsFinished)
                CloseSubActivity();
            return;
        }

        // ESC exits beach
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            IsFinished = true;
            return;
        }

        // Update hover highlights
        UpdateHighlights(local, delta);

        // Handle clicks
        if (leftPressed && local.Y >= 56 && local.Y < 552)
        {
            if (Raylib.CheckCollisionPointRec(local, FarmZone))
            {
                // TODO: open farm activity
            }
            else if (Raylib.CheckCollisionPointRec(local, SandcastleZone))
            {
                // TODO: open sandcastle interior
            }
            else if (local.Y < 240)
            {
                // TODO: open fishing
            }
            else
            {
                // Move player
                var target = ClampToWalkable(local);
                _moveTarget = target;
            }
        }

        // Update player movement
        UpdatePlayer(delta);
    }

    private void UpdateHighlights(Vector2 local, float delta)
    {
        _hoveredZone = "";
        float fadeSpeed = 8f;

        bool inOcean = Raylib.CheckCollisionPointRec(local, OceanZone) && local.Y >= 56;
        bool inSandcastle = Raylib.CheckCollisionPointRec(local, SandcastleZone);
        bool inFarm = Raylib.CheckCollisionPointRec(local, FarmZone);

        if (inOcean) _hoveredZone = "ocean";
        else if (inSandcastle) _hoveredZone = "sandcastle";
        else if (inFarm) _hoveredZone = "farm";

        _oceanHighlight += (inOcean ? fadeSpeed : -fadeSpeed) * delta;
        _sandcastleHighlight += (inSandcastle ? fadeSpeed : -fadeSpeed) * delta;
        _farmHighlight += (inFarm ? fadeSpeed : -fadeSpeed) * delta;

        _oceanHighlight = Math.Clamp(_oceanHighlight, 0f, 1f);
        _sandcastleHighlight = Math.Clamp(_sandcastleHighlight, 0f, 1f);
        _farmHighlight = Math.Clamp(_farmHighlight, 0f, 1f);
    }

    private void UpdatePlayer(float delta)
    {
        if (_moveTarget is not { } target) return;

        var diff = target - _playerPos;
        var dist = diff.Length();

        if (dist < 5f)
        {
            _moveTarget = null;
            _playerFrame = 0; // Idle
            return;
        }

        var dir = Vector2.Normalize(diff);
        _playerPos += dir * _playerSpeed * delta;
        _playerFlipH = dir.X > 0;

        // Walk animation (frames 1-2)
        _playerAnimTimer += delta;
        if (_playerAnimTimer >= 0.125f) // 8 FPS
        {
            _playerAnimTimer -= 0.125f;
            _playerFrame = _playerFrame == 1 ? 2 : 1;
        }

        // Footstep sound
        _footstepTimer += delta;
        if (_footstepTimer >= 0.25f)
        {
            _footstepTimer -= 0.25f;
            _audio.Play("assets/audio/footstep.wav");
        }
    }

    private Vector2 ClampToWalkable(Vector2 point)
    {
        // Simple rectangular clamp for the walkable area
        return new Vector2(
            Math.Clamp(point.X, 20, 780),
            Math.Clamp(point.Y, 260, 555)
        );
    }

    public void Draw(Vector2 offset)
    {
        // Draw sub-activity over everything if open
        if (_activeSubActivity != null)
        {
            _activeSubActivity.Draw(_subActivityOffset);
            return;
        }

        // Background (200x150 scaled 4x to 800x600)
        var src = new Rectangle(0, 0, _bgTexture.Width, _bgTexture.Height);
        var dest = new Rectangle(offset.X, offset.Y, 800, 600);
        Raylib.DrawTexturePro(_bgTexture, src, dest, Vector2.Zero, 0f, Color.White);

        // Day/night overlay
        var overlay = TimeSystem.OverlayColor;
        if (overlay.A > 0)
            Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 600, overlay);

        // Zone highlights
        if (_oceanHighlight > 0.01f)
            Raylib.DrawRectangle((int)(offset.X + OceanZone.X), (int)(offset.Y + OceanZone.Y),
                (int)OceanZone.Width, (int)OceanZone.Height,
                new Color((byte)77, (byte)128, (byte)204, (byte)(_oceanHighlight * 20)));

        if (_sandcastleHighlight > 0.01f)
            Raylib.DrawRectangle((int)(offset.X + SandcastleZone.X), (int)(offset.Y + SandcastleZone.Y),
                (int)SandcastleZone.Width, (int)SandcastleZone.Height,
                new Color((byte)230, (byte)204, (byte)128, (byte)(_sandcastleHighlight * 25)));

        if (_farmHighlight > 0.01f)
            Raylib.DrawRectangle((int)(offset.X + FarmZone.X), (int)(offset.Y + FarmZone.Y),
                (int)FarmZone.Width, (int)FarmZone.Height,
                new Color((byte)102, (byte)179, (byte)77, (byte)(_farmHighlight * 20)));

        // Click marker
        if (_moveTarget is { } target)
        {
            Raylib.DrawCircleV(offset + target, 3f, new Color((byte)255, (byte)255, (byte)255, (byte)120));
        }

        // Player (16x16 frames at 4x scale = 64x64)
        _playerSheet.DrawFrame(
            _playerFrame,
            offset + _playerPos - new Vector2(32, 32), // Center the 64x64 sprite
            4f,
            _playerFlipH
        );

        // Top bar background
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 56,
            new Color((byte)30, (byte)30, (byte)35, (byte)180));

        // Labels
        Raylib.DrawText("Mouse House - Beach", (int)offset.X + 10, (int)offset.Y + 18, 20, Color.White);
        Raylib.DrawText("[ESC] Exit", (int)offset.X + 700, (int)offset.Y + 18, 16, Color.LightGray);

        // Zone label when hovering
        if (_hoveredZone != "")
        {
            string label = _hoveredZone switch
            {
                "ocean" => "Go Fishing",
                "sandcastle" => "Enter Sandcastle",
                "farm" => "Visit Farm",
                _ => ""
            };
            int tw = Raylib.MeasureText(label, 20);
            Raylib.DrawText(label, (int)(offset.X + 400 - tw / 2), (int)offset.Y + 570, 20, Color.White);
        }
    }

    public void Close()
    {
        IsFinished = true;
    }

    private void CloseSubActivity()
    {
        _activeSubActivity?.Close();
        _activeSubActivity = null;
    }
}
