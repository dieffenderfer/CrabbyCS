using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;
using MouseHouse.UI;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Slider for the right-click popup menu's font size. Live preview updates
/// PopupMenu.FontSize as the slider moves.
/// </summary>
public class MenuFontSizeActivity : IActivity
{
    public Vector2 PanelSize => new(380, 180);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int MinSize = 10;
    private const int MaxSize = 36;
    private const int SliderXInset = 24;
    private const int KnobR = 8;

    private readonly Action<int> _onChanged;
    private int _size;
    private bool _dragging;

    public MenuFontSizeActivity(int currentSize, Action<int> onChanged)
    {
        _size = Math.Clamp(currentSize, MinSize, MaxSize);
        _onChanged = onChanged;
    }

    public void Load() { }

    private float SliderY => 96;
    private float SliderLeft => FrameInset + SliderXInset;
    private float SliderRight => PanelSize.X - FrameInset - SliderXInset;
    private float SliderW => SliderRight - SliderLeft;
    private float KnobLocalX => SliderLeft + (float)(_size - MinSize) / (MaxSize - MinSize) * SliderW;

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;

        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        if (leftPressed)
        {
            // Knob or anywhere on the track grabs.
            if (local.Y >= SliderY - 14 && local.Y <= SliderY + 14
                && local.X >= SliderLeft - KnobR && local.X <= SliderRight + KnobR)
                _dragging = true;
        }
        if (leftReleased) _dragging = false;

        if (_dragging)
        {
            float t = Math.Clamp((local.X - SliderLeft) / SliderW, 0f, 1f);
            int s = (int)MathF.Round(MinSize + t * (MaxSize - MinSize));
            if (s != _size)
            {
                _size = s;
                _onChanged(_size);
            }
        }
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Menu Font Size", true);

        // Body
        float bodyY = FrameInset + RetroWidgets.TitleBarHeight + 4;
        RetroSkin.DrawText("Right-click menu font size:",
            (int)(panelOffset.X + 18), (int)(panelOffset.Y + bodyY + 8),
            RetroSkin.BodyText, 16);

        string label = $"{_size} px";
        int lw = RetroSkin.MeasureText(label, 22);
        RetroSkin.DrawText(label,
            (int)(panelOffset.X + (PanelSize.X - lw) / 2),
            (int)(panelOffset.Y + bodyY + 28),
            RetroSkin.BodyText, 22);

        // Slider track (sunken)
        float sy = panelOffset.Y + SliderY;
        var track = new Rectangle(panelOffset.X + SliderLeft, sy - 3,
            SliderW, 6);
        RetroSkin.DrawSunken(track, RetroSkin.Face);

        // Tick marks
        int[] ticks = { 12, 16, 20, 24, 28, 32 };
        foreach (var v in ticks)
        {
            float t = (v - MinSize) / (float)(MaxSize - MinSize);
            int tx = (int)(panelOffset.X + SliderLeft + t * SliderW);
            Raylib.DrawLine(tx, (int)sy + 8, tx, (int)sy + 14, RetroSkin.Shadow);
        }

        // Knob (raised square — Win9x scrollbar thumb style)
        var knob = new Rectangle(panelOffset.X + KnobLocalX - KnobR,
            sy - KnobR - 1, KnobR * 2, KnobR * 2 + 2);
        RetroSkin.DrawRaised(knob);

        // Live sample line
        RetroSkin.DrawText("Sample: The quick brown fox",
            (int)(panelOffset.X + 18), (int)(panelOffset.Y + PanelSize.Y - 32),
            RetroSkin.BodyText, _size);
    }

    public void Close() { }
}
