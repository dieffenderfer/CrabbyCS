using System.Diagnostics;
using System.Globalization;
using Raylib_cs;

namespace MouseHouse.Core;

/// <summary>
/// Streams a remote HTTP audio source. When ffmpeg is on PATH we own the PCM
/// samples in-process: ffmpeg decodes to s16le on stdout, a reader thread
/// fills a circular tape buffer, and a Raylib AudioStream is refilled from a
/// playhead that supports reverse and variable-speed scrubbing — the OB-4
/// "magic radio" model. When only ffplay/mpv is available we fall back to the
/// old "shell out and let it own audio" path; tape, scrubbing, and recording
/// are unavailable in that mode.
/// </summary>
public class RadioPlayer
{
    private const int RefillFrames = 1024;       // per-buffer refill chunk
    private const double LiveThresholdSeconds = 0.10;

    private static readonly string[] CandidateBackends = { "ffmpeg", "ffplay", "mpv" };
    private readonly Dictionary<string, string?> _detected = new();
    private readonly string? _streamBackend;     // ffplay/mpv if no ffmpeg
    private readonly string? _ffmpeg;            // null if unavailable

    // ── Stream-out backend (legacy) ──────────────────────────────────────
    private Process? _legacyProc;

    // ── Owned-PCM backend ────────────────────────────────────────────────
    private Process? _decoder;
    private Thread? _readerThread;
    private CancellationTokenSource? _readerCts;
    private RadioTape? _tape;
    private AudioStream _stream;
    private bool _streamLoaded;
    private double _playheadFrame;
    private double _velocity = 1.0;

    // ── Shadow decoder ───────────────────────────────────────────────────
    // Always-on silent pipeline for a single URL: ffmpeg keeps decoding,
    // a Raylib AudioStream is loaded and PlayAudioStream'd at volume 0,
    // and Pump() refills it every frame just like the main stream. When
    // the user switches to its URL we don't move anything — we just call
    // ActivateShadow() which unmutes its stream and stops the main
    // pipeline. Switching away mutes the shadow again. Always-ready,
    // genuinely instant. Easy to revert: delete the shadow fields and
    // the four *Shadow methods.
    private Process? _shadowDecoder;
    private Thread? _shadowThread;
    private CancellationTokenSource? _shadowCts;
    private RadioTape? _shadowTape;
    private string? _shadowUrl;
    private AudioStream _shadowStream;
    private bool _shadowStreamLoaded;
    private double _shadowPlayhead;
    private bool _shadowActive;        // shadow is the audible pipeline
    private readonly short[] _shadowRefillBuf = new short[RefillFrames * RadioTape.Channels];
    private RadioRecorder? _recorder;
    private string? _lastRecordingPath;
    private DateTime _recordingFlashUntil;
    private readonly short[] _refillBuf = new short[RefillFrames * RadioTape.Channels];
    private readonly object _refillLock = new();

    // Most recent mono samples we've sent to the audio device — drives the
    // visualizer's spectrum analysis. Captured from the same _refillBuf
    // contents that just played, so the visuals lock to what's audible.
    private const int MonoRingFrames = 4096;
    private readonly float[] _monoRing = new float[MonoRingFrames];
    private int _monoRingPos;
    private bool _monoHasData;
    private readonly object _monoLock = new();

    public string? CurrentStationName { get; private set; }
    public string? CurrentStationSlug { get; private set; }
    public string? CurrentUrl { get; private set; }
    public float Volume { get; private set; } = 0.6f;
    public bool BackendAvailable => _ffmpeg != null || _streamBackend != null;
    public string? BackendName => _ffmpeg ?? _streamBackend;
    public bool SupportsTape => _ffmpeg != null;

    public bool IsPlaying
    {
        get
        {
            if (_ffmpeg != null)
            {
                if (_shadowActive)
                    return _shadowDecoder != null && !_shadowDecoder.HasExited;
                return _decoder != null && !_decoder.HasExited;
            }
            return _legacyProc != null && !_legacyProc.HasExited;
        }
    }

    public double Velocity
    {
        get => _velocity;
        set => _velocity = Math.Clamp(value, -4.0, 4.0);
    }

    public bool IsRecording => _recorder != null && _recorder.IsRunning;
    public string? LastRecordingPath => _lastRecordingPath;
    public bool RecordingFlashActive => DateTime.UtcNow < _recordingFlashUntil;

    /// <summary>True when the playhead is sitting near WriteHead at +1× speed.</summary>
    public bool IsLive
    {
        get
        {
            if (_tape == null) return true;
            double ahead = (_tape.WriteHead - _playheadFrame) / RadioTape.SampleRate;
            return Math.Abs(_velocity - 1.0) < 0.02 && ahead < LiveThresholdSeconds + 0.05;
        }
    }

    /// <summary>How far behind the live edge the playhead is, in seconds (≥0).</summary>
    public double PlayheadSecondsAgo
    {
        get
        {
            if (_tape == null) return 0;
            return Math.Max(0, (_tape.WriteHead - _playheadFrame) / RadioTape.SampleRate);
        }
    }

    /// <summary>How many seconds of buffered tape exist behind the live edge.</summary>
    public double TapeOldestSecondsAgo
    {
        get
        {
            if (_tape == null) return 0;
            return (_tape.WriteHead - _tape.ValidStart) / (double)RadioTape.SampleRate;
        }
    }

    public RadioPlayer()
    {
        foreach (var name in CandidateBackends) _detected[name] = ResolveBackend(name);
        _ffmpeg = _detected["ffmpeg"];
        _streamBackend = _detected["ffplay"] ?? _detected["mpv"];
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    private static string? ResolveBackend(string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = name,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = "-version",
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            if (!p.WaitForExit(800)) { try { p.Kill(); } catch { } return null; }
            return name;
        }
        catch { return null; }
    }

    public void Play(string url, string name, float volume, string? slug = null)
    {
        // Shadow handoff: if a healthy shadow stream is already decoding
        // this URL, just unmute its audio output and stop the main pipeline
        // — instant playback because the shadow has been silently filling
        // its tape and feeding Raylib the whole time.
        if (_ffmpeg != null && _shadowUrl == url
            && _shadowDecoder != null && !_shadowDecoder.HasExited
            && _shadowStreamLoaded)
        {
            Volume = Math.Clamp(volume, 0, 1);
            CurrentStationName = name;
            CurrentStationSlug = slug;
            CurrentUrl = url;
            ActivateShadow();
            return;
        }

        // Switching to a non-shadow URL while the shadow was active: re-mute
        // the shadow but leave it running so it stays warm.
        if (_shadowActive)
        {
            _shadowActive = false;
            if (_shadowStreamLoaded) Raylib.SetAudioStreamVolume(_shadowStream, 0f);
        }

        Stop();
        Volume = Math.Clamp(volume, 0, 1);
        CurrentStationName = name;
        CurrentStationSlug = slug;
        CurrentUrl = url;

        if (_ffmpeg != null) StartOwnedPipeline(url);
        else if (_streamBackend != null) StartLegacyBackend(url);
    }

    private void ActivateShadow()
    {
        // Stop the main pipeline (audio + decoder) so we're not paying for
        // two streams; the shadow takes over as the audible source.
        StopMainOnly();
        _shadowActive = true;
        if (_shadowStreamLoaded) Raylib.SetAudioStreamVolume(_shadowStream, Volume);
    }

    /// <summary>
    /// Spawn a background ffmpeg for <paramref name="url"/> that fills its
    /// own tape silently, so a later Play() for the same URL can promote
    /// the shadow into the main slot instead of cold-starting. Call once
    /// when the radio widget first opens. No-op if shadow is already
    /// running for the same URL or if ffmpeg is unavailable.
    /// </summary>
    public void StartShadow(string url)
    {
        if (_ffmpeg == null) return;
        if (_shadowUrl == url && _shadowDecoder != null && !_shadowDecoder.HasExited) return;
        StopShadow();

        _shadowUrl = url;
        _shadowPlayhead = 0;
        _shadowActive = false;
        var tape = new RadioTape(60.0);
        _shadowTape = tape;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpeg!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("quiet");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(url);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-acodec"); psi.ArgumentList.Add("pcm_s16le");
            psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(RadioTape.SampleRate.ToString());
            psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add(RadioTape.Channels.ToString());
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("s16le");
            psi.ArgumentList.Add("pipe:1");
            _shadowDecoder = Process.Start(psi);
        }
        catch
        {
            _shadowDecoder = null;
            _shadowTape = null;
            _shadowUrl = null;
            return;
        }

        if (_shadowDecoder == null) { _shadowTape = null; _shadowUrl = null; return; }

        var dec = _shadowDecoder;
        _ = Task.Run(() => { try { dec.StandardError.BaseStream.CopyTo(Stream.Null); } catch { } });

        _shadowCts = new CancellationTokenSource();
        var token = _shadowCts.Token;
        var stdout = _shadowDecoder.StandardOutput.BaseStream;
        _shadowThread = new Thread(() => DecoderReaderLoop(stdout, tape, token))
        {
            IsBackground = true,
            Name = "RadioShadowDecoder",
        };
        _shadowThread.Start();

        // Wire the shadow's own AudioStream so it's actually playing the
        // whole time, just at volume 0 until the user switches to it.
        if (!_shadowStreamLoaded)
        {
            Raylib.SetAudioStreamBufferSizeDefault(RefillFrames);
            _shadowStream = Raylib.LoadAudioStream((uint)RadioTape.SampleRate, 16, (uint)RadioTape.Channels);
            _shadowStreamLoaded = true;
        }
        Raylib.SetAudioStreamVolume(_shadowStream, _shadowActive ? Volume : 0f);
        Raylib.PlayAudioStream(_shadowStream);
    }

    private void StopShadow()
    {
        try { _shadowCts?.Cancel(); } catch { }
        var dec = _shadowDecoder; _shadowDecoder = null;
        if (dec != null)
        {
            try { if (!dec.HasExited) dec.Kill(true); } catch { }
            try { dec.Dispose(); } catch { }
        }
        try { _shadowThread?.Join(500); } catch { }
        _shadowThread = null;
        _shadowCts?.Dispose();
        _shadowCts = null;
        _shadowTape = null;
        _shadowUrl = null;
        _shadowPlayhead = 0;
        _shadowActive = false;
        if (_shadowStreamLoaded)
        {
            try { Raylib.StopAudioStream(_shadowStream); } catch { }
            try { Raylib.UnloadAudioStream(_shadowStream); } catch { }
            _shadowStreamLoaded = false;
        }
    }

    private void StopMainOnly()
    {
        StopRecording();
        try { _readerCts?.Cancel(); } catch { }
        var dec = _decoder; _decoder = null;
        if (dec != null)
        {
            try { if (!dec.HasExited) dec.Kill(true); } catch { }
            try { dec.Dispose(); } catch { }
        }
        try { _readerThread?.Join(500); } catch { }
        _readerThread = null;
        _readerCts?.Dispose();
        _readerCts = null;
        if (_streamLoaded)
        {
            try { Raylib.StopAudioStream(_stream); } catch { }
            try { Raylib.UnloadAudioStream(_stream); } catch { }
            _streamLoaded = false;
        }
        _tape = null;
        _playheadFrame = 0;
        _velocity = 1.0;
        var lp = _legacyProc; _legacyProc = null;
        if (lp != null)
        {
            try { if (!lp.HasExited) lp.Kill(true); } catch { }
            try { lp.Dispose(); } catch { }
        }
    }

    public void SetVolume(float volume)
    {
        Volume = Math.Clamp(volume, 0, 1);
        if (_streamLoaded) Raylib.SetAudioStreamVolume(_stream, Volume);
        // Only the active shadow produces audible sound; an inactive shadow
        // stays at zero so it doesn't bleed under the main pipeline.
        if (_shadowStreamLoaded)
            Raylib.SetAudioStreamVolume(_shadowStream, _shadowActive ? Volume : 0f);
    }

    public void GoLive()
    {
        if (_tape == null) return;
        _playheadFrame = _tape.WriteHead - RadioTape.SampleRate * LiveThresholdSeconds;
        _velocity = 1.0;
    }

    /// <summary>Called once per frame from the host scene to refill the audio buffer.</summary>
    public void Pump()
    {
        if (_streamLoaded && _tape != null)
        {
            while (Raylib.IsAudioStreamProcessed(_stream))
                FillOneBuffer();
        }
        // Always pump the shadow (whether muted or not) so its tape's
        // playhead keeps pace with WriteHead and the buffer doesn't grow
        // unboundedly past the tape's capacity.
        if (_shadowStreamLoaded && _shadowTape != null)
        {
            while (Raylib.IsAudioStreamProcessed(_shadowStream))
                FillShadowBuffer();
        }
    }

    private void FillShadowBuffer()
    {
        var tape = _shadowTape;
        if (tape == null) return;
        for (int i = 0; i < RefillFrames; i++)
        {
            tape.ReadFrame(_shadowPlayhead, out short l, out short r);
            _shadowRefillBuf[i * 2]     = l;
            _shadowRefillBuf[i * 2 + 1] = r;
            _shadowPlayhead += 1.0;
        }
        // Keep the playhead within the resident window. If the shadow's
        // decoder lags, hold near WriteHead so the audio doesn't read
        // garbage; if WriteHead pulls way ahead, jump up so we stay near
        // live.
        double minPos = tape.ValidStart + 1;
        double maxPos = tape.WriteHead - 1;
        if (_shadowPlayhead < minPos) _shadowPlayhead = minPos;
        if (_shadowPlayhead > maxPos) _shadowPlayhead = maxPos;
        unsafe
        {
            fixed (short* p = _shadowRefillBuf)
                Raylib.UpdateAudioStream(_shadowStream, p, RefillFrames);
        }
        // When the shadow is the audible pipeline, mirror its samples into
        // the visualizer's mono ring so the spectrum locks to what's
        // actually playing.
        if (_shadowActive)
        {
            lock (_monoLock)
            {
                for (int i = 0; i < RefillFrames; i++)
                {
                    int l = _shadowRefillBuf[i * 2];
                    int r = _shadowRefillBuf[i * 2 + 1];
                    _monoRing[_monoRingPos] = (l + r) * (1f / 65536f);
                    _monoRingPos = (_monoRingPos + 1) % _monoRing.Length;
                }
                _monoHasData = true;
            }
        }
    }

    private void FillOneBuffer()
    {
        if (_tape == null) return;
        lock (_refillLock)
        {
            // Step the playhead by velocity per output frame.
            for (int i = 0; i < RefillFrames; i++)
            {
                _tape.ReadFrame(_playheadFrame, out short l, out short r);
                _refillBuf[i * 2]     = l;
                _refillBuf[i * 2 + 1] = r;
                _playheadFrame += _velocity;
            }

            // Clamp to the resident window so we don't drift off the tape.
            double minPos = _tape.ValidStart + 1;
            double maxPos = _tape.WriteHead - 1;
            if (_playheadFrame < minPos) { _playheadFrame = minPos; _velocity = Math.Max(_velocity, 0); }
            if (_playheadFrame > maxPos) { _playheadFrame = maxPos; _velocity = Math.Min(_velocity, 1.0); }

            unsafe
            {
                fixed (short* p = _refillBuf)
                    Raylib.UpdateAudioStream(_stream, p, RefillFrames);
            }

            if (_recorder != null && _recorder.IsRunning)
                _recorder.Write(_refillBuf, RefillFrames);

            // Mirror the same frames into the mono ring for the visualizer.
            lock (_monoLock)
            {
                for (int i = 0; i < RefillFrames; i++)
                {
                    int l = _refillBuf[i * 2];
                    int r = _refillBuf[i * 2 + 1];
                    _monoRing[_monoRingPos] = (l + r) * (1f / 65536f);
                    _monoRingPos = (_monoRingPos + 1) % _monoRing.Length;
                }
                _monoHasData = true;
            }
        }
    }

    public int SampleRate => RadioTape.SampleRate;

    /// <summary>
    /// Copy the most recent <c>dest.Length</c> mono samples into <paramref name="dest"/>.
    /// Returns true when we have live PCM (owned-pipeline mode); false when running
    /// the legacy ffplay/mpv backend or when the radio is off — in which case
    /// <paramref name="dest"/> is left untouched and the caller should fall back
    /// to its synthetic spectrum.
    /// </summary>
    public bool CopyRecentMono(float[] dest)
    {
        if (!_monoHasData || dest == null || dest.Length == 0) return false;
        int n = Math.Min(dest.Length, _monoRing.Length);
        lock (_monoLock)
        {
            int start = (_monoRingPos - n + _monoRing.Length) % _monoRing.Length;
            for (int i = 0; i < n; i++)
                dest[i] = _monoRing[(start + i) % _monoRing.Length];
        }
        // Pad if dest is larger than the ring (shouldn't normally happen).
        for (int i = n; i < dest.Length; i++) dest[i] = 0;
        return true;
    }

    // ── Owned PCM pipeline ───────────────────────────────────────────────
    private void StartOwnedPipeline(string url)
    {
        _tape = new RadioTape(60.0);
        _playheadFrame = 0;
        _velocity = 1.0;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpeg!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("quiet");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(url);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-acodec"); psi.ArgumentList.Add("pcm_s16le");
            psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(RadioTape.SampleRate.ToString());
            psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add(RadioTape.Channels.ToString());
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("s16le");
            psi.ArgumentList.Add("pipe:1");
            _decoder = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Radio] ffmpeg launch failed: {ex.Message}");
            _decoder = null;
            return;
        }

        if (_decoder == null) return;

        // Drain stderr so the child never blocks on a full pipe.
        _ = Task.Run(() => { try { _decoder.StandardError.BaseStream.CopyTo(Stream.Null); } catch { } });

        _readerCts = new CancellationTokenSource();
        var token = _readerCts.Token;
        var stdout = _decoder.StandardOutput.BaseStream;
        var tape = _tape;
        _readerThread = new Thread(() => DecoderReaderLoop(stdout, tape, token))
        {
            IsBackground = true,
            Name = "RadioDecoder",
        };
        _readerThread.Start();

        if (!_streamLoaded)
        {
            // Match Raylib's per-sub-buffer size to our refill chunk so each
            // UpdateAudioStream call fills the whole sub-buffer. The default
            // (4096 frames) is much larger than our refill, leaving the rest
            // of every sub-buffer playing stale samples — sounds awful.
            Raylib.SetAudioStreamBufferSizeDefault(RefillFrames);
            _stream = Raylib.LoadAudioStream((uint)RadioTape.SampleRate, 16, (uint)RadioTape.Channels);
            _streamLoaded = true;
        }
        Raylib.SetAudioStreamVolume(_stream, Volume);
        Raylib.PlayAudioStream(_stream);
    }

    private static void DecoderReaderLoop(Stream stdout, RadioTape tape, CancellationToken token)
    {
        const int chunk = 8192;
        byte[] bytes = new byte[chunk];
        short[] frames = new short[chunk / sizeof(short)];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int read = stdout.Read(bytes, 0, bytes.Length);
                if (read <= 0) break; // EOF / pipe closed
                int sampleCount = read / sizeof(short);
                Buffer.BlockCopy(bytes, 0, frames, 0, sampleCount * sizeof(short));
                int frameCount = sampleCount / RadioTape.Channels;
                if (frameCount > 0) tape.WriteSamples(frames, frameCount);
            }
        }
        catch { /* pipe killed during shutdown */ }
    }

    // ── Recording ────────────────────────────────────────────────────────
    public string? StartRecording()
    {
        if (!SupportsTape) return null;
        if (IsRecording) return _recorder?.Path;
        string slug = string.IsNullOrWhiteSpace(CurrentStationSlug) ? "radio" : CurrentStationSlug!;
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string dir = Path.Combine(SaveManager.SaveDirectory, "recordings");
        string path = Path.Combine(dir, $"{slug}-{stamp}.mp3");
        try
        {
            _recorder = new RadioRecorder(_ffmpeg!, path, RefillFrames);
            return path;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Radio] recording start failed: {ex.Message}");
            _recorder = null;
            return null;
        }
    }

    public string? StopRecording()
    {
        var rec = _recorder;
        _recorder = null;
        if (rec == null) return null;
        rec.Stop();
        _lastRecordingPath = rec.Path;
        _recordingFlashUntil = DateTime.UtcNow.AddSeconds(3);
        return rec.Path;
    }

    // ── Legacy fallback ──────────────────────────────────────────────────
    private void StartLegacyBackend(string url)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _streamBackend!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (_streamBackend == "ffplay")
            {
                psi.ArgumentList.Add("-nodisp");
                psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("quiet");
                psi.ArgumentList.Add("-af");
                psi.ArgumentList.Add($"volume={Volume.ToString("0.00", CultureInfo.InvariantCulture)}");
                psi.ArgumentList.Add(url);
            }
            else
            {
                psi.ArgumentList.Add("--no-video");
                psi.ArgumentList.Add("--really-quiet");
                psi.ArgumentList.Add($"--volume={(int)(Volume * 100)}");
                psi.ArgumentList.Add(url);
            }
            _legacyProc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Radio] legacy play failed: {ex.Message}");
            _legacyProc = null;
        }
    }

    public void Stop()
    {
        // Stop recording first so we capture the tail.
        StopRecording();
        StopShadow();

        // Tear down owned pipeline.
        try { _readerCts?.Cancel(); } catch { }
        var dec = _decoder; _decoder = null;
        if (dec != null)
        {
            try { if (!dec.HasExited) dec.Kill(true); } catch { }
            try { dec.Dispose(); } catch { }
        }
        try { _readerThread?.Join(500); } catch { }
        _readerThread = null;
        _readerCts?.Dispose();
        _readerCts = null;

        if (_streamLoaded)
        {
            try { Raylib.StopAudioStream(_stream); } catch { }
            try { Raylib.UnloadAudioStream(_stream); } catch { }
            _streamLoaded = false;
        }
        _tape = null;
        _playheadFrame = 0;
        _velocity = 1.0;

        // Tear down legacy.
        var lp = _legacyProc; _legacyProc = null;
        if (lp != null)
        {
            try { if (!lp.HasExited) lp.Kill(true); } catch { }
            try { lp.Dispose(); } catch { }
        }

        CurrentUrl = null;
    }
}
