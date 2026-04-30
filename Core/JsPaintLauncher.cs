using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MouseHouse.Core;

public static class JsPaintLauncher
{
    public static Process? Launch()
    {
        var resolved = ResolveCompanionPath();
        if (resolved is null)
        {
            Console.Error.WriteLine("[JsPaint] companion executable not found beside main app");
            return null;
        }

        var (path, isDll) = resolved.Value;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = isDll ? "dotnet" : path,
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            if (isDll) psi.ArgumentList.Add(path);
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JsPaint] failed to launch companion: {ex.Message}");
            return null;
        }
    }

    private static (string Path, bool IsDll)? ResolveCompanionPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var companionDir = Path.Combine(baseDir, "MouseHousePaint");

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "MouseHouse.Paint.exe"
            : "MouseHouse.Paint";

        var exe = Path.Combine(companionDir, exeName);
        if (File.Exists(exe)) return (exe, false);

        var dll = Path.Combine(companionDir, "MouseHouse.Paint.dll");
        if (File.Exists(dll)) return (dll, true);

        return null;
    }
}
