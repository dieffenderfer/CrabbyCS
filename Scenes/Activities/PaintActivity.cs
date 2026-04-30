using System.Diagnostics;
using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class PaintActivity : IActivity
{
    public Vector2 PanelSize => new(420, 200);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private Process? _process;
    private string _status = "Opening jspaint…";
    private bool _launchFailed;

    public PaintActivity(AssetCache assets)
    {
        _assets = assets;
    }

    public void Load()
    {
        _process = JsPaintLauncher.Launch();
        if (_process is null)
        {
            _launchFailed = true;
            _status = "Could not start the Paint window.\nIs the MouseHousePaint companion installed?";
            return;
        }

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => IsFinished = true;
        _status = "jspaint is open in another window.\nClose that window — or click below — when done.";
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        if (_process is { HasExited: true })
        {
            IsFinished = true;
            return;
        }

        if (leftPressed)
        {
            var local = mousePos - panelOffset;
            var btn = CloseButtonRect();
            if (Raylib.CheckCollisionPointRec(local, btn))
            {
                Close();
            }
        }
    }

    public void Draw(Vector2 offset)
    {
        var w = (int)PanelSize.X;
        var h = (int)PanelSize.Y;
        var ox = (int)offset.X;
        var oy = (int)offset.Y;

        Raylib.DrawRectangle(ox, oy, w, h, new Color((byte)40, (byte)42, (byte)50, (byte)255));
        Raylib.DrawRectangle(ox, oy, w, 36, new Color((byte)60, (byte)64, (byte)76, (byte)255));
        FontManager.DrawText("Paint", ox + 12, oy + 10, 18, Color.White);

        FontManager.DrawText(_status, ox + 16, oy + 56, 14, Color.LightGray);

        var btn = CloseButtonRect();
        var btnColor = _launchFailed
            ? new Color((byte)180, (byte)80, (byte)80, (byte)255)
            : new Color((byte)100, (byte)130, (byte)200, (byte)255);
        Raylib.DrawRectangle(ox + (int)btn.X, oy + (int)btn.Y, (int)btn.Width, (int)btn.Height, btnColor);
        FontManager.DrawText(_launchFailed ? "Dismiss" : "Close Paint",
            ox + (int)btn.X + 16, oy + (int)btn.Y + 10, 14, Color.White);
    }

    private static Rectangle CloseButtonRect() => new(150, 140, 120, 36);

    public void Close()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.CloseMainWindow();
        }
        catch { }

        try
        {
            if (_process is { HasExited: false })
            {
                if (!_process.WaitForExit(500))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch { }

        IsFinished = true;
    }
}
