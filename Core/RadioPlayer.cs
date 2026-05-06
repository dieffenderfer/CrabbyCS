using System.Diagnostics;
using System.Globalization;

namespace MouseHouse.Core;

/// <summary>
/// Plays an HTTP audio stream by spawning ffplay (or mpv) as a child process.
/// Raylib's audio API works on local files, not on continuous internet
/// streams, so we hand off to a real media player. The child is killed when
/// playback stops or when the host process exits.
/// </summary>
public class RadioPlayer
{
    private Process? _process;
    private readonly string? _backend;

    public string? CurrentStationName { get; private set; }
    public string? CurrentUrl { get; private set; }
    public float Volume { get; private set; } = 0.6f;
    public bool BackendAvailable => _backend != null;
    public string? BackendName => _backend;
    public bool IsPlaying => _process != null && !_process.HasExited;

    public RadioPlayer()
    {
        _backend = DetectBackend();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    private static string? DetectBackend()
    {
        foreach (var name in new[] { "ffplay", "mpv" })
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
                if (p == null) continue;
                if (!p.WaitForExit(800)) { try { p.Kill(); } catch { } }
                return name;
            }
            catch { /* not on PATH; try the next one */ }
        }
        return null;
    }

    public void Play(string url, string name, float volume)
    {
        Stop();
        Volume = Math.Clamp(volume, 0, 1);
        CurrentStationName = name;
        CurrentUrl = url;
        if (_backend == null) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _backend,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (_backend == "ffplay")
            {
                psi.ArgumentList.Add("-nodisp");
                psi.ArgumentList.Add("-loglevel");
                psi.ArgumentList.Add("quiet");
                psi.ArgumentList.Add("-af");
                psi.ArgumentList.Add($"volume={Volume.ToString("0.00", CultureInfo.InvariantCulture)}");
                psi.ArgumentList.Add(url);
            }
            else // mpv
            {
                psi.ArgumentList.Add("--no-video");
                psi.ArgumentList.Add("--really-quiet");
                psi.ArgumentList.Add($"--volume={(int)(Volume * 100)}");
                psi.ArgumentList.Add(url);
            }
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Radio] play failed: {ex.Message}");
            _process = null;
        }
    }

    public void Stop()
    {
        var p = _process;
        _process = null;
        CurrentUrl = null;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(true); } catch { }
        try { p.Dispose(); } catch { }
    }
}
