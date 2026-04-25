using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class FontSizeActivity : IActivity
{
    public Vector2 PanelSize => new(360, 160);
    public bool IsFinished { get; private set; }

    private readonly Action<int> _onSizeChanged;
    private int _size;
    private bool _draggingSlider;

    private const int TitleBarH = 28;
    private const int SliderX = 24;
    private const int SliderW = 312;
    private const int SliderY = 80;
    private const int KnobRadius = 10;
    private const int MinSize = 8;
    private const int MaxSize = 64;

    public FontSizeActivity(int currentSize, Action<int> onSizeChanged)
    {
        _size = currentSize;
        _onSizeChanged = onSizeChanged;
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

            float knobX = absSliderX + SizeToT(_size) * SliderW;
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
            int newSize = (int)MathF.Round(MinSize + t * (MaxSize - MinSize));
            if (newSize != _size)
            {
                _size = newSize;
                FontManager.SetLoadSize(_size);
                _onSizeChanged(_size);
            }
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

        string label = $"{_size}px";
        int labelW = Raylib.MeasureText(label, 20);
        Raylib.DrawText(label, px + (pw - labelW) / 2, py + TitleBarH + 10, 20, new Color(200, 200, 220, 255));

        float sliderX = px + SliderX;
        float sliderY = py + SliderY;

        Raylib.DrawLineEx(new Vector2(sliderX, sliderY), new Vector2(sliderX + SliderW, sliderY),
            3, new Color(80, 80, 90, 255));

        int[] ticks = { 8, 16, 24, 32, 40, 48, 56, 64 };
        foreach (int v in ticks)
        {
            float tx = sliderX + SizeToT(v) * SliderW;
            bool isDefault = v == FontManager.DefaultLoadSize;
            Raylib.DrawLineEx(new Vector2(tx, sliderY - (isDefault ? 8 : 6)),
                new Vector2(tx, sliderY + (isDefault ? 8 : 6)),
                isDefault ? 2 : 1,
                isDefault ? new Color(140, 140, 150, 255) : new Color(100, 100, 110, 200));
        }

        float knobX = sliderX + SizeToT(_size) * SliderW;
        Raylib.DrawCircle((int)knobX, (int)sliderY, KnobRadius, new Color(100, 160, 240, 255));
        Raylib.DrawCircle((int)knobX, (int)sliderY, KnobRadius - 3, new Color(60, 120, 200, 255));

        FontManager.DrawText("The quick brown fox jumps!", px + SliderX, py + SliderY + 24, 18, new Color(220, 220, 225, 255));
    }

    public void Close() { }

    private static float SizeToT(int size)
        => (float)(size - MinSize) / (MaxSize - MinSize);
}
