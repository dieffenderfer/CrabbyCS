using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseHouse.Core;

/// <summary>
/// JSON-based persistence to a platform-appropriate directory.
/// </summary>
public static class SaveManager
{
    private static readonly JsonSerializerOptions JsonOpts = MakeJsonOpts();

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MouseHouse persists a small fixed set of POCOs and intentionally uses reflection-based serialisation; types are kept by feature use.")]
    private static JsonSerializerOptions MakeJsonOpts() => new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    private static string? _saveDir;
    private static string _appFolderName = "MouseHouse";

    /// <summary>
    /// The leaf folder name appended to the platform user-data root
    /// (~/Library/Application Support/&lt;name&gt;, %APPDATA%\&lt;name&gt;,
    /// ~/.local/share/&lt;name&gt;). Default is "MouseHouse" for the main pet
    /// app. Sibling executables that ship as their own product (e.g. the
    /// standalone radio for itch.io) set this once at startup before any
    /// save/load so they get their own data directory and don't collide
    /// with the pet's settings.
    /// Setter must be called before the first <see cref="SaveDirectory"/>
    /// access — once resolved, the path is cached.
    /// </summary>
    public static string AppFolderName
    {
        get => _appFolderName;
        set
        {
            _appFolderName = value;
            _saveDir = null;
        }
    }

    public static string SaveDirectory
    {
        get
        {
            if (_saveDir != null) return _saveDir;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                _saveDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", _appFolderName);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                _saveDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    _appFolderName);
            else // Linux
                _saveDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", _appFolderName);

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
