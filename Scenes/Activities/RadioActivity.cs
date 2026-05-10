using System.Numerics;
using MouseHouse.Core;
using MouseHouse.Data;
using MouseHouse.UI;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Companion-process wrapper around <see cref="RadioWidget"/> + <see cref="RadioPlayer"/>.
/// Hosts the radio in its own OS window so the pet's main window stays
/// pinned topmost while the radio gets normal z-level (other apps can
/// stack over it). The audio decoder, tape buffer, and metadata polling
/// all run in this process — completely isolated from the pet's main
/// thread, so the pet's animation never hitches on radio I/O.
/// </summary>
public class RadioActivity : IActivity
{
    private readonly RadioPlayer _player = new();
    private readonly RadioWidget _widget;
    private bool _finished;

    public RadioActivity()
    {
        _widget = new RadioWidget(_player);
        _widget.StateChanged = OnWidgetStateChanged;
    }

    // The inline station-library editor used to grow this panel to
    // 400×450 when open; that path was retired in favour of launching
    // stations.json in the user's text editor on Shift+Right-Click,
    // so the panel is now fixed at the widget's natural size.
    public Vector2 PanelSize => new(RadioWidget.W, RadioWidget.H);
    public bool IsFinished => _finished;

    public void Load()
    {
        _widget.Visible = true;
        // The widget's Position controls where it draws — in this dedicated
        // host window we want it flush against the OS window's origin so
        // the host's title-bar drag zone (top FrameInset + TitleBarHeight)
        // aligns with the widget's drawn title bar.
        _widget.Position = Vector2.Zero;

        // Restore station/volume/viz from the radio-owned settings file
        // (separate from PetSettings.json so the pet and radio processes
        // never race on the same JSON write).
        var s = SaveManager.LoadOrDefault<RadioPrefs>(RadioPrefs.Filename);
        _widget.Restore(s.StationIdx, s.Volume, s.VizMode);
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        // Audio pump — refills the Raylib audio stream from the tape buffer
        // before every frame so playback never starves.
        _player.Pump();
        _widget.Position = panelOffset;
        _widget.Update(delta, mousePos, leftPressed, leftReleased, rightPressed);
    }

    public void Draw(Vector2 panelOffset)
    {
        _widget.Position = panelOffset;
        _widget.Draw();
    }

    public void Close()
    {
        // Persist final state so the next launch resumes on the same
        // station / volume / viz mode.
        SaveRadioPrefs();
        _player.Stop();
    }

    /// <summary>
    /// Hooked into <see cref="RadioWidget.StateChanged"/> — the widget fires
    /// this whenever the user toggles state via the chrome (close X, station
    /// next/prev, volume drag, viz cycle, etc). Use it to (1) finalize the
    /// activity if the X was clicked and (2) keep radio.json in sync with
    /// in-flight changes so a force-quit doesn't lose the current settings.
    /// </summary>
    private void OnWidgetStateChanged()
    {
        if (!_widget.Visible) _finished = true;
        SaveRadioPrefs();
    }

    private void SaveRadioPrefs()
    {
        SaveManager.Save(RadioPrefs.Filename, new RadioPrefs
        {
            StationIdx = _widget.StationIndex,
            Volume = _widget.Volume,
            VizMode = _widget.VizMode,
        });
    }

    /// <summary>
    /// Persisted radio settings — owned exclusively by the radio companion
    /// process. The pet reads only its own PetSettings.RadioOpen flag for
    /// session-restore; everything else (selected station, volume, viz)
    /// is tracked here so the two processes never write the same file.
    /// </summary>
    private class RadioPrefs
    {
        public const string Filename = "radio.json";
        public int StationIdx { get; set; }
        public float Volume { get; set; } = 0.6f;
        public int VizMode { get; set; }
    }
}
