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
    private RadioRecorder? _recorder;
    private string? _lastRecordingPath;
    private DateTime _recordingFlashUntil;
    private readonly short[] _refillBuf = new short[RefillFrames * RadioTape.Channels];
    private readonly object _refillLock = new();

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
            if (_ffmpeg != null) return _decoder != null && !_decoder.HasExited;
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
        Stop();
        Volume = Math.Clamp(volume, 0, 1);
        CurrentStationName = name;
        CurrentStationSlug = slug;
        CurrentUrl = url;

        if (_ffmpeg != null) StartOwnedPipeline(url);
        else if (_streamBackend != null) StartLegacyBackend(url);
    }

    public void SetVolume(float volume)
    {
        Volume = Math.Clamp(volume, 0, 1);
        if (_streamLoaded) Raylib.SetAudioStreamVolume(_stream, Volume);
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
        if (!_streamLoaded || _tape == null) return;
        // Defensively: if the decoder died, surface that by leaving the stream
        // empty and letting IsPlaying flip to false.
        while (Raylib.IsAudioStreamProcessed(_stream))
            FillOneBuffer();
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
        }
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
