using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MouseHouse.Core;

/// <summary>
/// Tiny cross-platform native file-dialog wrapper. Used by PaintActivity so
/// saves go through the OS picker and the user always knows the resulting
/// path. Returns null when the user cancels.
/// </summary>
public static class NativeFileDialog
{
    public static string? Save(string title, string defaultName, string[] extensions)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return SaveMac(title, defaultName);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return SaveWin(title, defaultName, extensions);
            return SaveLinux(title, defaultName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NativeFileDialog] Save failed: {ex.Message}");
            return null;
        }
    }

    public static string? Open(string title, string[] extensions)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return OpenMac(title, extensions);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return OpenWin(title, extensions);
            return OpenLinux(title, extensions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NativeFileDialog] Open failed: {ex.Message}");
            return null;
        }
    }

    // ── macOS via osascript ─────────────────────────────────────────────
    private static string? SaveMac(string title, string defaultName)
    {
        // `choose file name` returns a file alias (or errors -128 on cancel).
        var script =
            $"set theFile to choose file name with prompt \"{Esc(title)}\" default name \"{Esc(defaultName)}\"\n" +
            "POSIX path of theFile";
        return RunOsascript(script);
    }

    private static string? OpenMac(string title, string[] exts)
    {
        var typeList = string.Join(", ", exts.Select(e => $"\"{e.TrimStart('.')}\""));
        var script =
            $"set theFile to choose file with prompt \"{Esc(title)}\" of type {{{typeList}}}\n" +
            "POSIX path of theFile";
        return RunOsascript(script);
    }

    private static string? RunOsascript(string script)
    {
        var psi = new ProcessStartInfo("osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Pass each line of the script as its own -e arg so quoting works cleanly.
        foreach (var line in script.Split('\n'))
        {
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(line);
        }
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            // -128 means user cancelled — that's not an error worth surfacing.
            if (!stderr.Contains("-128")) Console.Error.WriteLine($"[osascript] {stderr.Trim()}");
            return null;
        }
        var path = stdout.Trim();
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ── Windows via comdlg32 ────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAMEW
    {
        public int      lStructSize;
        public IntPtr   hwndOwner;
        public IntPtr   hInstance;
        public string?  lpstrFilter;
        public string?  lpstrCustomFilter;
        public int      nMaxCustFilter;
        public int      nFilterIndex;
        public IntPtr   lpstrFile;
        public int      nMaxFile;
        public IntPtr   lpstrFileTitle;
        public int      nMaxFileTitle;
        public string?  lpstrInitialDir;
        public string?  lpstrTitle;
        public int      Flags;
        public short    nFileOffset;
        public short    nFileExtension;
        public string?  lpstrDefExt;
        public IntPtr   lCustData;
        public IntPtr   lpfnHook;
        public string?  lpTemplateName;
        public IntPtr   pvReserved;
        public int      dwReserved;
        public int      FlagsEx;
    }

    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_PATHMUSTEXIST   = 0x00000800;
    private const int OFN_FILEMUSTEXIST   = 0x00001000;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAMEW ofn);
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    private static string? SaveWin(string title, string defaultName, string[] exts)
    {
        return RunCommonDialog(title, defaultName, exts, save: true);
    }

    private static string? OpenWin(string title, string[] exts)
    {
        return RunCommonDialog(title, "", exts, save: false);
    }

    private static string? RunCommonDialog(string title, string defaultName, string[] exts, bool save)
    {
        // Build "PNG (*.png)\0*.png\0BMP (*.bmp)\0*.bmp\0All files\0*.*\0\0"
        var sb = new StringBuilder();
        foreach (var e in exts)
        {
            string ext = e.TrimStart('.');
            sb.Append(ext.ToUpperInvariant()).Append(" (*.").Append(ext).Append(")\0*.")
              .Append(ext).Append('\0');
        }
        sb.Append("All files\0*.*\0\0");
        string filter = sb.ToString();

        const int bufLen = 4096;
        IntPtr buf = Marshal.AllocHGlobal(bufLen * 2);
        try
        {
            // Pre-fill the buffer with the default name.
            byte[] defBytes = Encoding.Unicode.GetBytes(defaultName + "\0");
            Marshal.Copy(defBytes, 0, buf, defBytes.Length);
            for (int i = defBytes.Length; i < bufLen * 2; i++)
                Marshal.WriteByte(buf, i, 0);

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                lpstrFilter = filter,
                lpstrFile = buf,
                nMaxFile = bufLen,
                lpstrTitle = title,
                lpstrDefExt = exts.Length > 0 ? exts[0].TrimStart('.') : null,
                Flags = save ? (OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST)
                             : (OFN_FILEMUSTEXIST  | OFN_PATHMUSTEXIST),
            };

            bool ok = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
            if (!ok) return null;
            return Marshal.PtrToStringUni(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ── Linux via zenity (best-effort fallback) ─────────────────────────
    private static string? SaveLinux(string title, string defaultName)
    {
        var psi = new ProcessStartInfo("zenity",
            $"--file-selection --save --confirm-overwrite --title=\"{title}\" --filename=\"{defaultName}\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        return RunCapture(psi);
    }
    private static string? OpenLinux(string title, string[] exts)
    {
        var filterArg = string.Join(" ", exts.Select(e => $"--file-filter=\"*.{e.TrimStart('.')}\""));
        var psi = new ProcessStartInfo("zenity",
            $"--file-selection --title=\"{title}\" {filterArg}")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        return RunCapture(psi);
    }
    private static string? RunCapture(ProcessStartInfo psi)
    {
        try
        {
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && outp.Length > 0 ? outp : null;
        }
        catch { return null; }
    }
}
