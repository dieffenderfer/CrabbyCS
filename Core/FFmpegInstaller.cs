using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace MouseHouse.Core;

/// <summary>
/// In-app downloader for an ffmpeg binary. The radio falls back to this
/// when <see cref="RadioPlayer"/> can't find ffmpeg in any of the
/// bundled / save-dir / PATH locations. On click, fetches a static build
/// from a pinned HTTPS source (gyan.dev on Windows, evermeet.cx on
/// macOS), extracts the <c>ffmpeg</c> executable out of the archive,
/// and drops it in <c>&lt;SaveDir&gt;/bin/ffmpeg(.exe)</c> — exactly
/// where <see cref="RadioPlayer.Recheck"/> looks for it. Linux is
/// surfaced as a manual-install page since static builds there ship
/// as .tar.xz which the framework can't decompress without an extra
/// dependency, and most distros have ffmpeg one package-manager
/// command away anyway.
/// </summary>
public sealed class FFmpegInstaller
{
    public enum InstallState
    {
        Idle,
        Downloading,
        Extracting,
        Verifying,
        Done,
        Failed,
    }

    // Pinned mirrors. HTTPS only; URLs hard-coded so a typo'd config
    // file or env-var can never redirect this to a malicious archive.
    // gyan.dev hosts the official Windows static build; evermeet.cx
    // is the long-standing curator of macOS binaries.
    private const string WindowsZipUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string MacOsZipUrl =
        "https://evermeet.cx/ffmpeg/ffmpeg.zip";

    public const string ManualInstallUrl = "https://ffmpeg.org/download.html";

    public InstallState State { get; private set; } = InstallState.Idle;
    public long BytesDownloaded { get; private set; }
    /// <summary>Server-reported content length, or null if the server
    /// didn't return one (chunked / redirect chains). The widget shows
    /// raw bytes in that case instead of a percentage.</summary>
    public long? TotalBytes { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? InstalledPath { get; private set; }

    public bool CanAutoInstall =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public bool IsRunning =>
        State == InstallState.Downloading
        || State == InstallState.Extracting
        || State == InstallState.Verifying;

    /// <summary>
    /// Reset internal progress state and kick off a background install
    /// task. Safe to call again after a failure to retry. Does nothing
    /// while an install is already running.
    /// </summary>
    public void StartInstall()
    {
        if (IsRunning) return;
        State = InstallState.Downloading;
        BytesDownloaded = 0;
        TotalBytes = null;
        ErrorMessage = null;
        InstalledPath = null;

        _ = Task.Run(RunInstallAsync);
    }

    private async Task RunInstallAsync()
    {
        try
        {
            string targetDir = Path.Combine(SaveManager.SaveDirectory, "bin");
            Directory.CreateDirectory(targetDir);
            string targetExe = Path.Combine(targetDir,
                OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

            string url;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) url = WindowsZipUrl;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) url = MacOsZipUrl;
            else throw new PlatformNotSupportedException(
                "Auto-install isn't available on this platform. " +
                "Install ffmpeg via your package manager (e.g. apt install ffmpeg) " +
                $"or download from {ManualInstallUrl}.");

            // Stage the download to a temp file (could be ~100 MB on
            // Windows). Streaming into memory would work but blows the
            // process working set for the duration of the extract step.
            string tempZip = Path.Combine(targetDir, ".ffmpeg-download.tmp");
            try
            {
                await DownloadToFileAsync(url, tempZip).ConfigureAwait(false);
                State = InstallState.Extracting;
                ExtractFFmpegFromZip(tempZip, targetExe);
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }

            // Unix needs the executable bit set or Process.Start will
            // ENOEXEC. chmod +x via the runtime's UnixFileMode API
            // (.NET 7+) so we don't shell out to /bin/chmod.
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(targetExe,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Downloaded ffmpeg but couldn't make it executable: {ex.Message}");
                }
            }

            State = InstallState.Verifying;
            if (!RunVersionCheck(targetExe))
                throw new InvalidOperationException(
                    "Downloaded ffmpeg but it didn't run cleanly. " +
                    "Try again or install manually from " + ManualInstallUrl + ".");

            InstalledPath = targetExe;
            State = InstallState.Done;
        }
        catch (Exception ex)
        {
            ErrorMessage = ShortenError(ex);
            State = InstallState.Failed;
        }
    }

    private async Task DownloadToFileAsync(string url, string destPath)
    {
        // Single-shot HttpClient — the install is one-and-done so we
        // don't bother with a long-lived static instance. Default
        // timeout is 100 s which is way too short for ~100 MB on a
        // slow link; bump it.
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CrabbyRadio/1.0 (+ffmpeg-installer)");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        TotalBytes = resp.Content.Headers.ContentLength;

        await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 81920, useAsync: true);

        var buf = new byte[81920];
        while (true)
        {
            int n = await src.ReadAsync(buf.AsMemory()).ConfigureAwait(false);
            if (n <= 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
            BytesDownloaded += n;
        }
    }

    private static void ExtractFFmpegFromZip(string zipPath, string destExe)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        // gyan.dev nests the binary under a versioned folder like
        // ffmpeg-7.0.2-essentials_build/bin/ffmpeg.exe; evermeet's zip
        // is flat with just "ffmpeg" at the root. Scan all entries and
        // pick the first one whose filename is exactly "ffmpeg(.exe)"
        // so both layouts (and any future re-org) work without tweaks.
        string wanted = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        ZipArchiveEntry? match = null;
        foreach (var e in zip.Entries)
        {
            // Skip directory entries (have a zero length name).
            if (string.IsNullOrEmpty(e.Name)) continue;
            if (string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                match = e;
                break;
            }
        }
        if (match == null)
            throw new InvalidOperationException(
                $"Archive didn't contain '{wanted}'. " +
                "Mirror layout may have changed — try again or install manually.");

        // Extract to a sibling temp file then rename into place so an
        // interrupted extract (process killed mid-copy) doesn't leave a
        // half-written ffmpeg.exe that the player would try to launch.
        string tmp = destExe + ".partial";
        try
        {
            using (var entryStream = match.Open())
            using (var fs = File.Create(tmp))
            {
                entryStream.CopyTo(fs);
            }
            if (File.Exists(destExe)) File.Delete(destExe);
            File.Move(tmp, destExe);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static bool RunVersionCheck(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string ShortenError(Exception ex)
    {
        // Surface a one-liner the user can read in the LCD strip
        // without overflowing the tape row. The full exception still
        // hits stderr via the Console.Error.WriteLine inside the
        // caller's log line, if any.
        var msg = ex.Message;
        if (msg.Length > 120) msg = msg[..117] + "...";
        return msg;
    }
}
