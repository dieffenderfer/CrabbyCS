using MouseHouse.Core;

namespace MouseHouse.Data;

/// <summary>
/// Persisted pet preferences: color mode, scale, mute state.
/// </summary>
public class PetSettings
{
    public string ColorMode { get; set; } = "2color"; // "2color", "1color", "fullcolor"
    public float ScaleOverride { get; set; } = 0; // 0 = default
    public bool Muted { get; set; } = false;
    public bool PinkNose { get; set; } = false;
    public string FontFile { get; set; } = FontManager.DefaultFontFile;
    public string FontFilter { get; set; } = "Point";
    public int FontLoadSize { get; set; } = FontManager.DefaultLoadSize;

    private const string Filename = "settings.json";

    public static PetSettings Load()
    {
        var s = SaveManager.LoadOrDefault<PetSettings>(Filename);
        // One-time migration: when the app default font changed from Tiny5 to
        // W95FA, existing installs kept the old saved value. Carry them forward
        // unless they explicitly picked something else.
        if (s.FontFile == "Tiny5.ttf") s.FontFile = "W95F.otf";
        return s;
    }

    public void Save()
    {
        SaveManager.Save(Filename, this);
    }
}
