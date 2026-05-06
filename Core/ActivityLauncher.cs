using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MouseHouse.Core;

/// <summary>
/// Spawns a retro activity in the sibling MouseHouse.Activities executable so
/// the activity window has its own OS-level Z order — i.e. it can be put
/// behind other apps' windows like a normal app, while the pet's main window
/// stays pinned above.
/// </summary>
public static class ActivityLauncher
{
    public static Process? Launch(int activityId, string theme = "",
                                  int bodyFontSize = 16,
                                  int titleFontSize = 16,
                                  int statusFontSize = 14)
    {
        var resolved = ResolveCompanionPath();
        if (resolved is null)
        {
            Console.Error.WriteLine("[Activities] sibling executable not found; falling back to inline render");
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
            psi.ArgumentList.Add($"--id={activityId}");
            if (!string.IsNullOrEmpty(theme)) psi.ArgumentList.Add($"--theme={theme}");
            psi.ArgumentList.Add($"--body={bodyFontSize}");
            psi.ArgumentList.Add($"--title={titleFontSize}");
            psi.ArgumentList.Add($"--status={statusFontSize}");
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Activities] failed to launch sibling: {ex.Message}");
            return null;
        }
    }

    private static (string Path, bool IsDll)? ResolveCompanionPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = Path.Combine(baseDir, "MouseHouseActivities");
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "MouseHouse.Activities.exe"
            : "MouseHouse.Activities";
        var exe = Path.Combine(dir, exeName);
        if (File.Exists(exe)) return (exe, false);
        var dll = Path.Combine(dir, "MouseHouse.Activities.dll");
        if (File.Exists(dll)) return (dll, true);
        return null;
    }
}
