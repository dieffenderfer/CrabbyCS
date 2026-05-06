using System.Diagnostics;

namespace MouseHouse.Core;

/// <summary>
/// Pipes raw post-scrub PCM (s16le 44100/stereo) into an ffmpeg child that
/// encodes to MP3. The radio mixer calls <see cref="Write"/> with the same
/// buffer it just shipped to Raylib, so the recording matches what the user
/// actually hears (reversed, slowed down, etc.).
/// </summary>
public sealed class RadioRecorder : IDisposable
{
    private Process? _ff;
    private Stream? _stdin;
    private readonly byte[] _scratch;
    private readonly object _lock = new();
    private bool _stopped;

    public string Path { get; }
    public bool IsRunning => _ff != null && !_stopped;
    public DateTime StartedUtc { get; }

    public RadioRecorder(string ffmpegPath, string outPath, int bufferFrames)
    {
        Path = outPath;
        StartedUtc = DateTime.UtcNow;
        _scratch = new byte[bufferFrames * RadioTape.Channels * sizeof(short) * 2];

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(RadioTape.SampleRate.ToString());
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add(RadioTape.Channels.ToString());
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");
        psi.ArgumentList.Add("-codec:a"); psi.ArgumentList.Add("libmp3lame");
        psi.ArgumentList.Add("-qscale:a"); psi.ArgumentList.Add("2");
        psi.ArgumentList.Add(outPath);

        _ff = Process.Start(psi);
        _stdin = _ff?.StandardInput.BaseStream;
        // Drain stdout/stderr so the child never blocks on full pipes.
        _ff?.StandardOutput.BaseStream.Dispose();
        if (_ff != null)
        {
            _ = Task.Run(() => { try { _ff.StandardError.BaseStream.CopyTo(Stream.Null); } catch { } });
        }
    }

    public void Write(short[] frames, int frameCount)
    {
        if (frameCount <= 0) return;
        lock (_lock)
        {
            if (_stopped || _stdin == null) return;
            int byteCount = frameCount * RadioTape.Channels * sizeof(short);
            if (_scratch.Length < byteCount) return; // shouldn't happen
            Buffer.BlockCopy(frames, 0, _scratch, 0, byteCount);
            try { _stdin.Write(_scratch, 0, byteCount); }
            catch { _stopped = true; }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_stopped) return;
            _stopped = true;
            try { _stdin?.Flush(); } catch { }
            try { _stdin?.Dispose(); } catch { }
            _stdin = null;
        }
        var ff = _ff;
        _ff = null;
        if (ff == null) return;
        try
        {
            if (!ff.WaitForExit(4000))
            {
                try { ff.Kill(true); } catch { }
            }
        }
        catch { }
        try { ff.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
