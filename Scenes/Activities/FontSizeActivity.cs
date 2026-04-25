using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class FontSizeActivity : IActivity
{
    public Vector2 PanelSize => new(360, 160);
    public bool IsFinished { get; private set; }

    private readonly Action<float> _onScaleChanged;
    private float _scale;
    private bool _draggingSlider;

    private const int TitleBarH = 28;
    private const int SliderX = 24;
    private const int SliderW = 312;
    private const int SliderY = 80;
    private const int KnobRadius = 10;
    private const float MinScale = 0.5f;
    private const float MaxScale = 3.0f;

    public FontSizeActivity(float currentScale, Action<float> onScaleChanged)
    {
        _scale = currentScale;
        _onScaleChanged = onScaleChanged;
    }

    public void Load() { }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
        bool leftPressed, bool leftReleased, bool rightPressed)
    {
        float absSliderX = panelOffset.X + SliderX;
        float absSliderY = panelOffset.Y + SliderY;

        if (leftPressed)
        {
            var closeRect = new Rectangle(
                panelOffset.X + PanelSize.X - 40, panelOffset.Y, 40, TitleBarH);
            if (Raylib.CheckCollisionPointRec(mousePos, closeRect))
            {
                IsFinished = true;
                return;
            }

            float knobX = absSliderX + ScaleToT(_scale) * SliderW;
            if (Vector2.Distance(mousePos, new Vector2(knobX, absSliderY)) < KnobRadius + 6
                || (mousePos.Y >= absSliderY - 12 && mousePos.Y <= absSliderY + 12
                    && mousePos.X >= absSliderX && mousePos.X <= absSliderX + SliderW))
            {
                _draggingSlider = true;
            }
        }

        if (leftReleased)
            _draggingSlider = false;

        if (_draggingSlider)
        {
            float t = Math.Clamp((mousePos.X - absSliderX) / SliderW, 0f, 1f);
            _scale = TToScale(t);
            _scale = MathF.Round(_scale * 20f) / 20f;
            FontManager.SizeScale = _scale;
            _onScaleChanged(_scale);
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
            IsFinished = true;
    }

    public void Draw(Vector2 offset)
    {
        int px = (int)offset.X, py = (int)offset.Y;
        int pw = (int)PanelSize.X, ph = (int)PanelSize.Y;

        Raylib.DrawRectangle(px, py, pw, ph, new Color(25, 25, 30, 255));
        Raylib.DrawRectangleLines(px, py, pw, ph, new Color(60, 60, 65, 255));

        Raylib.DrawRectangle(px, py, pw, TitleBarH, new Color(45, 45, 50, 255));
        Raylib.DrawText("Font Size", px + 10, py + 7, 14, new Color(200, 200, 200, 255));
        Raylib.DrawText("[X]", px + pw - 36, py + 7, 14, new Color(200, 100, 100, 255));

        string label = $"{_scale:F2}x";
        int labelW = Raylib.MeasureText(label, 20);
        Raylib.DrawText(label, px + (pw - labelW) / 2, py + TitleBarH + 10, 20, new Color(200, 200, 220, 255));

        float sliderX = px + SliderX;
        float sliderY = py + SliderY;

        Raylib.DrawLineEx(new Vector2(sliderX, sliderY), new Vector2(sliderX + SliderW, sliderY),
            3, new Color(80, 80, 90, 255));

        float[] ticks = { 0.5f, 1.0f, 1.5f, 2.0f, 2.5f, 3.0f };
        foreach (float v in ticks)
        {
            float tx = sliderX + ScaleToT(v) * SliderW;
            Raylib.DrawLineEx(new Vector2(tx, sliderY - 6), new Vector2(tx, sliderY + 6),
                1, new Color(100, 100, 110, 200));
        }

        float oneX = sliderX + ScaleToT(1.0f) * SliderW;
        Raylib.DrawLineEx(new Vector2(oneX, sliderY - 8), new Vector2(oneX, sliderY + 8),
            2, new Color(140, 140, 150, 255));

        float knobX = sliderX + ScaleToT(_scale) * SliderW;
        Raylib.DrawCircle((int)knobX, (int)sliderY, KnobRadius, new Color(100, 160, 240, 255));
        Raylib.DrawCircle((int)knobX, (int)sliderY, KnobRadius - 3, new Color(60, 120, 200, 255));

        string sampleText = "The quick brown fox jumps!";
        FontManager.DrawText(sampleText, px + SliderX, py + SliderY + 24, 18, new Color(220, 220, 225, 255));
    }

    public void Close() { }

    private static float ScaleToT(float scale)
        => (scale - MinScale) / (MaxScale - MinScale);

    private static float TToScale(float t)
        => MinScale + t * (MaxScale - MinScale);
}
