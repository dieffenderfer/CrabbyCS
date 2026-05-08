using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MouseHouse.Core;

/// <summary>
/// Best-effort cross-platform "is there an image on the system clipboard?"
/// reader. Used by PaintActivity so Ctrl+V can paste a screenshot copied from
/// another app. Returns null on miss/error so the caller can fall back to its
/// own internal clipboard.
/// </summary>
public static class SystemClipboard
{
    /// <summary>Tries to grab an image from the system clipboard. Returns
    /// the path to a temp PNG on success (caller deletes it), or null.</summary>
    public static string? TryGetImageToTempPng()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"mh_paint_paste_{Guid.NewGuid():N}.png");
        try
        {
            bool ok = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? TryGetMac(path)
                   : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? TryGetWindows(path)
                   : TryGetLinux(path);
            if (ok && File.Exists(path) && new FileInfo(path).Length > 0) return path;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SystemClipboard] {ex.Message}");
        }
        if (File.Exists(path)) try { File.Delete(path); } catch { /* ignore */ }
        return null;
    }

    // ── macOS via osascript ─────────────────────────────────────────────
    // The AppleScript «class PNGf» literal contains the U+00AB / U+00BB
    // chevrons. They round-trip cleanly through ProcessStartInfo.ArgumentList
    // because that path is UTF-8.
    private static bool TryGetMac(string outPath)
    {
        var script = new[]
        {
            "try",
            "  set pngData to the clipboard as «class PNGf»",
            "  set f to open for access POSIX file \"" + outPath.Replace("\"", "\\\"") + "\" with write permission",
            "  set eof f to 0",
            "  write pngData to f",
            "  close access f",
            "  return \"ok\"",
            "on error",
            "  return \"empty\"",
            "end try",
        };
        var psi = new ProcessStartInfo("osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var line in script) { psi.ArgumentList.Add("-e"); psi.ArgumentList.Add(line); }
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd().Trim();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 && stdout == "ok";
    }

    // ── Windows via PowerShell + System.Windows.Forms.Clipboard ─────────
    private static bool TryGetWindows(string outPath)
    {
        // PowerShell here-string is hard to escape; pass via stdin instead.
        var script =
            "Add-Type -AssemblyName System.Windows.Forms; " +
            "Add-Type -AssemblyName System.Drawing; " +
            "$img = [System.Windows.Forms.Clipboard]::GetImage(); " +
            "if ($img -ne $null) { " +
              $"$img.Save('{outPath.Replace("'", "''")}', " +
              "[System.Drawing.Imaging.ImageFormat]::Png); " +
              "Write-Output 'ok' " +
            "} else { Write-Output 'empty' }";
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -STA -Command -")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.WriteLine(script);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd().Trim();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 && stdout.EndsWith("ok");
    }

    // ── Linux via xclip ─────────────────────────────────────────────────
    private static bool TryGetLinux(string outPath)
    {
        var psi = new ProcessStartInfo("xclip",
            "-selection clipboard -t image/png -o")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        using var fs = File.OpenWrite(outPath);
        p.StandardOutput.BaseStream.CopyTo(fs);
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
