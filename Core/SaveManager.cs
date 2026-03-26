using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseHouse.Core;

/// <summary>
/// JSON-based persistence to a platform-appropriate directory.
/// </summary>
public static class SaveManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    private static string? _saveDir;

    public static string SaveDirectory
    {
        get
        {
            if (_saveDir != null) return _saveDir;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                _saveDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "MouseHouse");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                _saveDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MouseHouse");
            else // Linux
                _saveDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "MouseHouse");

            Directory.CreateDirectory(_saveDir);
            return _saveDir;
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026")]
    public static void Save<T>(string filename, T data)
    {
        var path = Path.Combine(SaveDirectory, filename);
        var json = JsonSerializer.Serialize(data, JsonOpts);
        File.WriteAllText(path, json);
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026")]
    public static T? Load<T>(string filename) where T : class
    {
        var path = Path.Combine(SaveDirectory, filename);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static T LoadOrDefault<T>(string filename) where T : class, new()
    {
        return Load<T>(filename) ?? new T();
    }
}
