using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Drum Machine — a 4-track × 16-step sequencer. Drum samples are
/// synthesized once into WAV files inside the save directory the first
/// time the activity is opened (kick: low sine sweep, snare: noise + tone,
/// hi-hat: high-passed noise, clap: paired noise bursts). After that the
/// activity just loads those WAVs via the normal Raylib.LoadSound path,
/// so there's no DSP cost at runtime.
///
/// Patterns persist to <c>drum_pattern.json</c>.
/// </summary>
public class DrumMachineActivity : IActivity
{
    public Vector2 PanelSize => new(660, 320);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int CellW = 30;
    private const int CellH = 28;
    private const int CellGap = 3;
    private const int LabelW = 60;
    private const int GridX = LabelW + 16;
    private const int Tracks = 4;
    private const int Steps = 16;
    private const float MinBpm = 60f;
    private const float MaxBpm = 200f;
    private const string PatternFile = "drum_pattern.json";

    private static readonly string[] TrackLabels = { "KICK", "SNARE", "HAT", "CLAP" };
    private static readonly string[] TrackFiles =
        { "drum_kick.wav", "drum_snare.wav", "drum_hat.wav", "drum_clap.wav" };

    private record SavedPattern(bool[,] Cells, float Bpm);

    private readonly bool[,] _cells = new bool[Tracks, Steps];
    private float _bpm = 110f;
    private bool _playing;
    private float _stepTimer;
    private int _stepCursor;
    private readonly Sound[] _samples = new Sound[Tracks];
    private bool _samplesLoaded;
    private string _status = "Click cells to build a beat.";
    private int _bpmDragging = -1;

    public void Load()
    {
        EnsureDrumSamples();
        for (int i = 0; i < Tracks; i++)
        {
            try
            {
                var path = Path.Combine(SaveManager.SaveDirectory, "drums", TrackFiles[i]);
                _samples[i] = Raylib.LoadSound(path);
            }
            catch { /* leave Sound default — Play is a no-op then */ }
        }
        _samplesLoaded = true;
        LoadPattern();
    }

    public void Close()
    {
        SavePattern();
        if (_samplesLoaded)
        {
            for (int i = 0; i < _samples.Length; i++)
            {
                try { Raylib.UnloadSound(_samples[i]); } catch { }
            }
            _samplesLoaded = false;
        }
        IsFinished = true;
    }

    private void LoadPattern()
    {
        try
        {
            var path = Path.Combine(SaveManager.SaveDirectory, PatternFile);
            if (!File.Exists(path)) return;
            // Custom shape because System.Text.Json struggles with multi-dim arrays —
            // store a flat list and a tempo, reshape on load.
            var raw = File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("Bpm", out var b)) _bpm = Math.Clamp(b.GetSingle(), MinBpm, MaxBpm);
            if (root.TryGetProperty("Cells", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                int i = 0;
                foreach (var v in c.EnumerateArray())
                {
                    if (i >= Tracks * Steps) break;
                    _cells[i / Steps, i % Steps] = v.GetBoolean();
                    i++;
                }
            }
        }
        catch { /* malformed file → leave defaults */ }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Anonymous flat-record shape; reflection-based JSON is intentional.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Same — primitives + bool[] only.")]
    private void SavePattern()
    {
        try
        {
            var flat = new bool[Tracks * Steps];
            for (int t = 0; t < Tracks; t++)
                for (int s = 0; s < Steps; s++)
                    flat[t * Steps + s] = _cells[t, s];
            var path = Path.Combine(SaveManager.SaveDirectory, PatternFile);
            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { Bpm = _bpm, Cells = flat }, opts));
        }
        catch { /* best-effort */ }
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        switch (RetroWidgets.MenuBarHitTest(menuBar,
                    new[] { _playing ? "Stop" : "Play", "Clear", "Demo" }, local, leftPressed))
        {
            case 0:
                _playing = !_playing;
                _stepTimer = 0;
                _stepCursor = 0;
                _status = _playing ? "Playing..." : "Stopped.";
                return;
            case 1:
                for (int t = 0; t < Tracks; t++)
                    for (int s = 0; s < Steps; s++) _cells[t, s] = false;
                SavePattern();
                _status = "Cleared.";
                return;
            case 2:
                LoadDemoPattern();
                SavePattern();
                _status = "Loaded demo pattern.";
                return;
        }

        // Cell toggles
        if (leftPressed)
        {
            for (int t = 0; t < Tracks; t++)
            {
                for (int s = 0; s < Steps; s++)
                {
                    if (RetroSkin.PointInRect(local, CellRect(t, s)))
                    {
                        _cells[t, s] = !_cells[t, s];
                        SavePattern();
                        if (_cells[t, s]) Trigger(t);
                        return;
                    }
                }
            }
            if (RetroSkin.PointInRect(local, BpmSliderRect()))
            {
                _bpmDragging = 1;
                UpdateBpmFromMouse(local);
                return;
            }
        }
        if (_bpmDragging > 0)
        {
            if (Raylib.IsMouseButtonDown(MouseButton.Left)) UpdateBpmFromMouse(local);
            else { _bpmDragging = -1; SavePattern(); }
        }

        // Sequencer tick — at every step boundary play whichever cells are
        // active in that column, then advance.
        if (_playing)
        {
            float secPerStep = 60f / _bpm / 4f;  // 16th-note grid (4 steps per beat).
            _stepTimer += delta;
            while (_stepTimer >= secPerStep)
            {
                _stepTimer -= secPerStep;
                for (int t = 0; t < Tracks; t++)
                    if (_cells[t, _stepCursor]) Trigger(t);
                _stepCursor = (_stepCursor + 1) % Steps;
            }
        }
    }

    private void Trigger(int track)
    {
        try { Raylib.PlaySound(_samples[track]); } catch { }
    }

    private void LoadDemoPattern()
    {
        for (int t = 0; t < Tracks; t++)
            for (int s = 0; s < Steps; s++) _cells[t, s] = false;
        // Boom-bap-ish: kick on 1+9, snare on 5+13, hat every other step,
        // clap doubling the snare for thickness.
        int[] kicks = { 0, 8 };
        int[] snares = { 4, 12 };
        int[] hats = { 0, 2, 4, 6, 8, 10, 12, 14 };
        int[] claps = { 4, 12 };
        foreach (var s in kicks) _cells[0, s] = true;
        foreach (var s in snares) _cells[1, s] = true;
        foreach (var s in hats) _cells[2, s] = true;
        foreach (var s in claps) _cells[3, s] = true;
    }

    private Rectangle CellRect(int track, int step)
    {
        float startY = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 16;
        float x = GridX + step * (CellW + CellGap);
        float y = startY + track * (CellH + CellGap);
        return new Rectangle(x, y, CellW, CellH);
    }

    private Rectangle BpmSliderRect()
    {
        float y = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
                  + 16 + Tracks * (CellH + CellGap) + 12;
        return new Rectangle(GridX, y, Steps * (CellW + CellGap) - CellGap, 12);
    }

    private void UpdateBpmFromMouse(Vector2 local)
    {
        var r = BpmSliderRect();
        float t = (local.X - r.X) / r.Width;
        _bpm = MinBpm + Math.Clamp(t, 0f, 1f) * (MaxBpm - MinBpm);
        _status = $"{(int)_bpm} BPM";
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Drum Machine", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar,
            new[] { _playing ? "Stop" : "Play", "Clear", "Demo" }, -1);

        for (int t = 0; t < Tracks; t++)
        {
            // Track label.
            var labelR = CellRect(t, 0);
            FontManager.DrawText(TrackLabels[t],
                (int)(panelOffset.X + labelR.X - LabelW - 6),
                (int)(panelOffset.Y + labelR.Y + 6),
                14, RetroSkin.BodyText);

            for (int s = 0; s < Steps; s++)
            {
                var r = CellRect(t, s);
                var abs = new Rectangle(panelOffset.X + r.X, panelOffset.Y + r.Y, r.Width, r.Height);
                bool on = _cells[t, s];
                bool atCursor = _playing && _stepCursor == s;

                if (on)
                {
                    Raylib.DrawRectangleRec(abs, atCursor ? RetroSkin.Highlight : RetroSkin.TitleActive);
                    Raylib.DrawRectangleLinesEx(abs, 1, RetroSkin.DarkShadow);
                }
                else
                {
                    RetroSkin.DrawSunken(abs, atCursor ? RetroSkin.Highlight : RetroSkin.SunkenBg);
                }
                // Beat markers on every 4th step — thin top accent so the
                // user can read the bar without counting.
                if (s % 4 == 0)
                {
                    Raylib.DrawRectangle((int)abs.X, (int)(abs.Y - 4),
                        (int)abs.Width, 2, RetroSkin.Shadow);
                }
            }
        }

        // BPM slider
        var bpm = BpmSliderRect();
        var bpmAbs = new Rectangle(panelOffset.X + bpm.X, panelOffset.Y + bpm.Y,
            bpm.Width, bpm.Height);
        RetroSkin.DrawSunken(bpmAbs, RetroSkin.SunkenBg);
        float frac = (_bpm - MinBpm) / (MaxBpm - MinBpm);
        var thumb = new Rectangle(bpmAbs.X + frac * (bpmAbs.Width - 16),
            bpmAbs.Y - 2, 16, bpmAbs.Height + 4);
        RetroSkin.DrawRaised(thumb);

        FontManager.DrawText("BPM",
            (int)(panelOffset.X + bpm.X - LabelW - 6),
            (int)(panelOffset.Y + bpm.Y - 2),
            14, RetroSkin.BodyText);
        FontManager.DrawText($"{(int)_bpm}",
            (int)(panelOffset.X + bpm.X + bpm.Width + 8),
            (int)(panelOffset.Y + bpm.Y - 2),
            14, RetroSkin.BodyText);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        int active = 0;
        for (int t = 0; t < Tracks; t++)
            for (int s = 0; s < Steps; s++) if (_cells[t, s]) active++;
        RetroWidgets.StatusBar(status, _status, $"{active} hits  •  {(int)_bpm} BPM");
    }

    // ── Synthesis (writes WAV files once on first run) ─────────────────────

    private static void EnsureDrumSamples()
    {
        var dir = Path.Combine(SaveManager.SaveDirectory, "drums");
        Directory.CreateDirectory(dir);
        var paths = TrackFiles.Select(f => Path.Combine(dir, f)).ToArray();
        bool allPresent = paths.All(File.Exists);
        if (allPresent) return;

        WriteWav(paths[0], SynthKick());
        WriteWav(paths[1], SynthSnare());
        WriteWav(paths[2], SynthHat());
        WriteWav(paths[3], SynthClap());
    }

    private const int SampleRate = 22050;

    private static short[] SynthKick()
    {
        // Low sine with rapid frequency sweep (120 Hz → 40 Hz) and fast
        // exponential amplitude decay. Length ≈ 220 ms.
        int n = SampleRate / 4;
        var s = new short[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)SampleRate;
            double freq = 40 + (120 - 40) * Math.Exp(-t * 18);
            phase += 2 * Math.PI * freq / SampleRate;
            double env = Math.Exp(-t * 6.5);
            double sample = Math.Sin(phase) * env * 0.85;
            s[i] = ClampShort(sample);
        }
        return s;
    }

    private static short[] SynthSnare()
    {
        // Noise + 200 Hz body. Mid-fast decay (~150 ms).
        int n = SampleRate * 3 / 16;
        var s = new short[n];
        var rng = new Random(0xBEEF);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)SampleRate;
            double env = Math.Exp(-t * 18);
            double noise = (rng.NextDouble() * 2 - 1);
            double body = Math.Sin(2 * Math.PI * 200 * t) * 0.4;
            s[i] = ClampShort((noise * 0.8 + body) * env * 0.8);
        }
        return s;
    }

    private static short[] SynthHat()
    {
        // High-passed noise — emulated with two-sample differencing so we
        // don't have to write a real filter. Short decay (~70 ms).
        int n = SampleRate / 14;
        var s = new short[n];
        var rng = new Random(0xC0FFEE);
        double prev = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)SampleRate;
            double env = Math.Exp(-t * 50);
            double noise = (rng.NextDouble() * 2 - 1);
            double hp = noise - prev * 0.95;  // crude HP — emphasizes high freq.
            prev = noise;
            s[i] = ClampShort(hp * env * 0.65);
        }
        return s;
    }

    private static short[] SynthClap()
    {
        // Three short bursts of noise separated by tiny gaps so it reads
        // as a clap rather than a single noise blip.
        int n = SampleRate / 5;
        var s = new short[n];
        var rng = new Random(0xCAFE);
        int[] burstStarts = { 0, (int)(SampleRate * 0.015), (int)(SampleRate * 0.030) };
        int burstLen = (int)(SampleRate * 0.010);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)SampleRate;
            bool inBurst = burstStarts.Any(bs => i >= bs && i < bs + burstLen);
            double env = Math.Exp(-(t - 0.030) * 25);
            if (t < 0.030) env = 1.0;
            double amp = inBurst ? 0.85 : 0.0;
            double noise = (rng.NextDouble() * 2 - 1);
            s[i] = ClampShort(noise * amp * env);
        }
        return s;
    }

    private static short ClampShort(double d)
    {
        double v = d * 32760;
        if (v > 32760) v = 32760;
        if (v < -32760) v = -32760;
        return (short)v;
    }

    private static void WriteWav(string path, short[] samples)
    {
        // Standard 16-bit PCM mono WAV header (44 bytes) followed by samples.
        // No fancy options — just the simplest container that LoadSound accepts.
        int dataBytes = samples.Length * 2;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                       // PCM chunk size
        bw.Write((short)1);                 // format = PCM
        bw.Write((short)1);                 // channels = mono
        bw.Write(SampleRate);
        bw.Write(SampleRate * 2);           // byte rate
        bw.Write((short)2);                 // block align
        bw.Write((short)16);                // bits per sample
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        for (int i = 0; i < samples.Length; i++) bw.Write(samples[i]);
    }
}
