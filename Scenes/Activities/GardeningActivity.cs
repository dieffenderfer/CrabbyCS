using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class GardeningActivity : IActivity
{
    public Vector2 PanelSize => new(800, 600);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly AudioManager _audio;

    private static readonly string[] PlantTypes = { "sunflower", "mushroom", "clover", "dandelion", "berry", "acorn" };

    // 6 patches in 2x3 grid
    private readonly Patch[] _patches = new Patch[6];
    private static readonly Vector2[] PatchPositions =
    {
        new(60, 340), new(300, 340), new(540, 340),
        new(60, 480), new(300, 480), new(540, 480),
    };
    private const float PatchW = 160, PatchH = 72;
    private const float GrowTime = 5f;

    private string _message = "";
    private float _messageTimer;

    // Tool state
    private enum GardenTool { Plant, Water, Harvest }
    private GardenTool _tool = GardenTool.Plant;

    private Texture2D _bgTexture;
    private static readonly Random Rng = new();

    public GardeningActivity(AssetCache assets, AudioManager audio)
    {
        _assets = assets;
        _audio = audio;
    }

    public void Load()
    {
        _bgTexture = _assets.GetTexture("assets/sprites/beach_bg.png"); // Reuse beach bg for now
        for (int i = 0; i < 6; i++)
            _patches[i] = new Patch();
        _message = "Click a plot to plant! Then water to grow.";
        _messageTimer = 3f;
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;
        _messageTimer -= delta;

        // Grow watered plants
        for (int i = 0; i < 6; i++)
        {
            var p = _patches[i];
            if (p.Planted && p.Watered && p.Stage < 2)
            {
                p.GrowTimer -= delta;
                if (p.GrowTimer <= 0)
                {
                    p.Stage++;
                    p.Watered = false;
                    p.GrowTimer = GrowTime;
                    _audio.Play("assets/audio/grow.wav");
                }
            }
        }

        // Tool selection buttons
        if (leftPressed)
        {
            if (local.X >= 20 && local.X < 100 && local.Y >= 80 && local.Y < 110)
            { _tool = GardenTool.Plant; return; }
            if (local.X >= 110 && local.X < 190 && local.Y >= 80 && local.Y < 110)
            { _tool = GardenTool.Water; return; }
            if (local.X >= 200 && local.X < 290 && local.Y >= 80 && local.Y < 110)
            { _tool = GardenTool.Harvest; return; }
        }

        // Click patches
        if (leftPressed)
        {
            for (int i = 0; i < 6; i++)
            {
                var pos = PatchPositions[i];
                if (local.X >= pos.X && local.X < pos.X + PatchW
                    && local.Y >= pos.Y && local.Y < pos.Y + PatchH)
                {
                    HandlePatchClick(i);
                    break;
                }
            }
        }
    }

    private void HandlePatchClick(int index)
    {
        var p = _patches[index];
        switch (_tool)
        {
            case GardenTool.Plant:
                if (!p.Planted)
                {
                    p.PlantType = PlantTypes[Rng.Next(PlantTypes.Length)];
                    p.Planted = true;
                    p.Stage = 0;
                    p.Watered = false;
                    p.GrowTimer = GrowTime;
                    _audio.Play("assets/audio/plant.wav");
                    _message = $"Planted a {p.PlantType}!";
                    _messageTimer = 1.5f;
                }
                break;

            case GardenTool.Water:
                if (p.Planted && p.Stage < 2 && !p.Watered)
                {
                    p.Watered = true;
                    _audio.Play("assets/audio/water.wav");
                    _message = "Watered!";
                    _messageTimer = 1f;
                }
                break;

            case GardenTool.Harvest:
                if (p.Planted && p.Stage >= 2)
                {
                    _message = $"Harvested {p.PlantType}!";
                    _messageTimer = 2f;
                    _audio.Play("assets/audio/harvest.wav");
                    p.Planted = false;
                    p.Stage = 0;
                    p.Watered = false;
                    p.PlantType = "";
                }
                break;
        }
    }

    public void Draw(Vector2 offset)
    {
        // Green garden background
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 600,
            new Color((byte)80, (byte)140, (byte)60, (byte)255));

        // Sky
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 250,
            new Color((byte)130, (byte)200, (byte)255, (byte)255));

        // Ground
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y + 250, 800, 350,
            new Color((byte)120, (byte)90, (byte)50, (byte)255));

        // Patches
        for (int i = 0; i < 6; i++)
        {
            var pos = offset + PatchPositions[i];
            var p = _patches[i];

            // Soil
            Raylib.DrawRectangle((int)pos.X, (int)pos.Y, (int)PatchW, (int)PatchH,
                new Color((byte)80, (byte)55, (byte)30, (byte)255));
            Raylib.DrawRectangleLines((int)pos.X, (int)pos.Y, (int)PatchW, (int)PatchH,
                new Color((byte)60, (byte)40, (byte)20, (byte)255));

            // Watered indicator
            if (p.Watered)
                Raylib.DrawRectangle((int)pos.X, (int)pos.Y, (int)PatchW, (int)PatchH,
                    new Color((byte)40, (byte)80, (byte)160, (byte)40));

            if (p.Planted)
            {
                // Draw plant based on stage
                var plantColor = p.Stage switch
                {
                    0 => new Color((byte)100, (byte)180, (byte)80, (byte)255),
                    1 => new Color((byte)60, (byte)200, (byte)60, (byte)255),
                    _ => new Color((byte)40, (byte)220, (byte)40, (byte)255),
                };

                // Simple plant drawing
                float cx = pos.X + PatchW / 2;
                float cy = pos.Y + PatchH - 5;
                int stemH = 15 + p.Stage * 12;
                Raylib.DrawLineEx(new Vector2(cx, cy), new Vector2(cx, cy - stemH), 3f, plantColor);

                if (p.Stage >= 1)
                {
                    // Leaves
                    Raylib.DrawCircleV(new Vector2(cx - 8, cy - stemH + 5), 6, plantColor);
                    Raylib.DrawCircleV(new Vector2(cx + 8, cy - stemH + 5), 6, plantColor);
                }
                if (p.Stage >= 2)
                {
                    // Flower/fruit
                    Raylib.DrawCircleV(new Vector2(cx, cy - stemH - 5), 8, Color.Yellow);
                }

                // Label
                FontManager.DrawText(p.PlantType, (int)pos.X + 5, (int)pos.Y + 2, 12, Color.White);

                // Growth progress
                if (p.Stage < 2 && p.Watered)
                {
                    float progress = 1f - p.GrowTimer / GrowTime;
                    Raylib.DrawRectangle((int)pos.X, (int)(pos.Y + PatchH - 4),
                        (int)(PatchW * progress), 4,
                        new Color((byte)100, (byte)255, (byte)100, (byte)180));
                }
            }
        }

        // Top bar
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 56,
            new Color((byte)30, (byte)60, (byte)25, (byte)220));
        FontManager.DrawText("Garden", (int)offset.X + 10, (int)offset.Y + 18, 20, Color.White);
        FontManager.DrawText("[ESC] Exit", (int)offset.X + 700, (int)offset.Y + 18, 16, Color.LightGray);

        // Tool buttons
        DrawToolButton(offset, 20, 80, "Plant", GardenTool.Plant);
        DrawToolButton(offset, 110, 80, "Water", GardenTool.Water);
        DrawToolButton(offset, 200, 80, "Harvest", GardenTool.Harvest);

        // Message
        if (_message != "" && _messageTimer > 0)
        {
            int tw = FontManager.MeasureText(_message, 22);
            FontManager.DrawText(_message, (int)(offset.X + 400 - tw / 2), (int)offset.Y + 140, 22, Color.White);
        }
    }

    private void DrawToolButton(Vector2 offset, int x, int y, string label, GardenTool tool)
    {
        bool selected = _tool == tool;
        var bg = selected ? new Color((byte)80, (byte)160, (byte)80, (byte)255)
                          : new Color((byte)60, (byte)100, (byte)60, (byte)255);
        Raylib.DrawRectangle((int)offset.X + x, (int)offset.Y + y, 80, 28, bg);
        Raylib.DrawRectangleLines((int)offset.X + x, (int)offset.Y + y, 80, 28, Color.DarkGreen);
        FontManager.DrawText(label, (int)offset.X + x + 8, (int)offset.Y + y + 6, 14,
            selected ? Color.White : Color.LightGray);
    }

    public void Close() => IsFinished = true;

    private class Patch
    {
        public bool Planted;
        public string PlantType = "";
        public int Stage; // 0, 1, 2
        public bool Watered;
        public float GrowTimer = GrowTime;
    }
}
