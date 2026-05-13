using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Sleep Sounds — a small ambient mixer. Each track is one looping
/// LoadMusicStream that runs in the background; a vertical fader sets its
/// volume independently. Combining several quiet layers (rain + fireplace,
/// ocean + wind, etc.) produces cozy beds without any DSP work.
///
/// Volumes persist to <c>sleep_sounds.json</c> so the user's preferred mix
/// comes back next time. Streams stop and unload when the panel closes.
/// </summary>
public class SleepSoundsActivity : IActivity
{
    public Vector2 PanelSize => new(60 + Tracks.Length * TrackColW + 40, 360);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int TrackColW = 80;
    private const int FaderH = 200;
    private const string SaveFileName = "sleep_sounds.json";

    private record TrackDef(string Label, string AssetPath);

    private static readonly TrackDef[] Tracks =
    {
        new("Rain",       "assets/core/sounds/rain_loop.wav"),
        new("Fireplace",  "assets/zones/sounds/fireplace.wav"),
        new("Ocean",      "assets/zones/sounds/ocean_waves.wav"),
        new("Wind",       "assets/kite/sounds/wind_gentle.wav"),
        new("Night",      "assets/stargazing/sounds/night_ambient.wav"),
    };

    private readonly AssetCache _assets;
    private readonly Music[] _streams;
    private readonly bool[] _loaded;
    private readonly float[] _volumes;
    private int _draggingTrack = -1;
    private string _status = "Drag a fader up to start a layer.";

    public SleepSoundsActivity(AssetCache assets)
    {
        _assets = assets;
        _streams = new Music[Tracks.Length];
        _loaded = new bool[Tracks.Length];
        _volumes = new float[Tracks.Length];
    }

    public void Load()
    {
        LoadSavedVolumes();
        for (int i = 0; i < Tracks.Length; i++)
        {
            var fullPath = Path.Combine(_assets.BasePath, Tracks[i].AssetPath);
            if (!File.Exists(fullPath))
            {
                _loaded[i] = false;
                continue;
            }
            try
            {
                _streams[i] = Raylib.LoadMusicStream(fullPath);
                _streams[i].Looping = true;
                _loaded[i] = true;
                // Streams always play; we modulate audibility via volume so
                // a fader change is instant rather than waiting for the
                // first PlayMusicStream call to spin up the decoder.
                Raylib.SetMusicVolume(_streams[i], _volumes[i]);
                Raylib.PlayMusicStream(_streams[i]);
            }
            catch
            {
                _loaded[i] = false;
            }
        }
    }

    private void LoadSavedVolumes()
    {
        try
        {
            var path = Path.Combine(SaveManager.SaveDirectory, SaveFileName);
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var arr = System.Text.Json.JsonSerializer.Deserialize<float[]>(json);
            if (arr == null) return;
            for (int i = 0; i < Tracks.Length && i < arr.Length; i++)
                _volumes[i] = Math.Clamp(arr[i], 0f, 1f);
        }
        catch { /* default to all-zero on parse error */ }
    }

    private void SaveVolumes()
    {
        try
        {
            Directory.CreateDirectory(SaveManager.SaveDirectory);
            var path = Path.Combine(SaveManager.SaveDirectory, SaveFileName);
            File.WriteAllText(path,
                System.Text.Json.JsonSerializer.Serialize(_volumes));
        }
        catch { /* best-effort */ }
    }

    public void Close()
    {
        SaveVolumes();
        for (int i = 0; i < _streams.Length; i++)
        {
            if (!_loaded[i]) continue;
            Raylib.StopMusicStream(_streams[i]);
            Raylib.UnloadMusicStream(_streams[i]);
            _loaded[i] = false;
        }
        IsFinished = true;
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        // Streams must be pumped every frame to keep playing.
        for (int i = 0; i < _streams.Length; i++)
            if (_loaded[i]) Raylib.UpdateMusicStream(_streams[i]);

        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "Silence", "Pink Noise Bed" }, local, leftPressed))
        {
            case 0:
                for (int i = 0; i < _volumes.Length; i++) _volumes[i] = 0f;
                ApplyVolumes();
                SaveVolumes();
                _status = "All faders down.";
                return;
            case 1:
                // Quick preset: a low rain + fireplace bed, others off.
                for (int i = 0; i < _volumes.Length; i++) _volumes[i] = 0f;
                if (Tracks.Length > 0) _volumes[0] = 0.35f;             // rain
                if (Tracks.Length > 1) _volumes[1] = 0.25f;             // fireplace
                ApplyVolumes();
                SaveVolumes();
                _status = "Preset: gentle rain + crackling fire.";
                return;
        }

        // Slider drag — first press starts a drag on whichever fader rect
        // the cursor is in, subsequent movement updates that fader only.
        if (leftPressed)
        {
            for (int i = 0; i < Tracks.Length; i++)
            {
                if (RetroSkin.PointInRect(local, FaderHitRect(i)))
                {
                    _draggingTrack = i;
                    UpdateVolumeFromMouse(i, local);
                    ApplyVolumes();
                    return;
                }
            }
        }
        if (_draggingTrack >= 0)
        {
            if (Raylib.IsMouseButtonDown(MouseButton.Left))
            {
                UpdateVolumeFromMouse(_draggingTrack, local);
                ApplyVolumes();
            }
            else
            {
                _draggingTrack = -1;
                SaveVolumes();
            }
        }
    }

    private void ApplyVolumes()
    {
        for (int i = 0; i < _streams.Length; i++)
            if (_loaded[i]) Raylib.SetMusicVolume(_streams[i], _volumes[i]);
    }

    private Rectangle FaderTrackRect(int i)
    {
        float startX = FrameInset + 40 + i * TrackColW;
        float startY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 40;
        return new Rectangle(startX + TrackColW / 2f - 4, startY, 8, FaderH);
    }

    private Rectangle FaderHitRect(int i)
    {
        // Generous hit area around the visible track so the cursor doesn't
        // have to land precisely on an 8px sliver to start a drag.
        var t = FaderTrackRect(i);
        return new Rectangle(t.X - 16, t.Y - 6, t.Width + 32, t.Height + 12);
    }

    private void UpdateVolumeFromMouse(int i, Vector2 local)
    {
        var t = FaderTrackRect(i);
        float y = Math.Clamp(local.Y, t.Y, t.Y + t.Height);
        float v = 1f - (y - t.Y) / t.Height;
        _volumes[i] = Math.Clamp(v, 0f, 1f);
        _status = _loaded[i]
            ? $"{Tracks[i].Label}: {(int)(_volumes[i] * 100)}%"
            : $"{Tracks[i].Label} not loaded (asset missing).";
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Sleep Sounds", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "Silence", "Pink Noise Bed" }, -1);

        // Faders
        for (int i = 0; i < Tracks.Length; i++)
        {
            var t = FaderTrackRect(i);
            var tAbs = new Rectangle(panelOffset.X + t.X, panelOffset.Y + t.Y,
                t.Width, t.Height);

            // Track: sunken vertical channel.
            RetroSkin.DrawSunken(tAbs, RetroSkin.SunkenBg);

            // Thumb position from volume — 0 sits at the bottom, 1 at the top.
            float v = _volumes[i];
            float thumbY = tAbs.Y + (1f - v) * tAbs.Height - 8;
            var thumb = new Rectangle(tAbs.X - 8, thumbY, 24, 14);
            RetroSkin.DrawRaised(thumb);
            // Notch line through the center of the thumb so it reads as a
            // mixer fader rather than a generic chip.
            Raylib.DrawRectangle((int)thumb.X + 2, (int)(thumb.Y + thumb.Height / 2),
                (int)thumb.Width - 4, 1, RetroSkin.DarkShadow);

            // Label (track name above, percentage below).
            string label = Tracks[i].Label;
            int lw = FontManager.MeasureText(label, 14);
            FontManager.DrawText(label,
                (int)(panelOffset.X + t.X + t.Width / 2f - lw / 2f),
                (int)(panelOffset.Y + t.Y - 22),
                14, _loaded[i] ? RetroSkin.BodyText : RetroSkin.DisabledText);

            string pct = $"{(int)(v * 100)}%";
            int pw = FontManager.MeasureText(pct, 12);
            FontManager.DrawText(pct,
                (int)(panelOffset.X + t.X + t.Width / 2f - pw / 2f),
                (int)(panelOffset.Y + t.Y + t.Height + 6),
                12, RetroSkin.BodyText);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        int active = 0;
        for (int i = 0; i < _volumes.Length; i++) if (_volumes[i] > 0.01f) active++;
        RetroWidgets.StatusBar(status, _status, $"{active} layer{(active == 1 ? "" : "s")} active");
    }
}
