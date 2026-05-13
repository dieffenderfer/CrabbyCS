using MouseHouse.Core;

namespace MouseHouse.Data;

/// <summary>
/// Persisted pet preferences: color mode, scale, mute state.
/// </summary>
public class PetSettings
{
    public string ColorMode { get; set; } = "2color"; // "2color", "1color", "fullcolor"
    public float ScaleOverride { get; set; } = 0; // 0 = default
    public float UIScaleOverride { get; set; } = 0; // 0 = platform default (2x Windows, 1x macOS)
    public bool Muted { get; set; } = false;
    public bool PinkNose { get; set; } = false;
    /// <summary>
    /// Cosmetic "amoeba dripped window" effect — green slime hanging from
    /// the bottom edge of windows. Defaults off so existing users aren't
    /// surprised by an effect they didn't ask for.
    /// </summary>
    public bool AmoebaDrips { get; set; } = false;
    public string FontFile { get; set; } = FontManager.DefaultFontFile;
    public string FontFilter { get; set; } = "Point";
    public int FontLoadSize { get; set; } = FontManager.DefaultLoadSize;
    public int MenuFontSize { get; set; } = 16;

    /// <summary>
    /// Whether the sibling-process radio companion was open at the last
    /// snapshot. The pet polls the child's HasExited per-frame so a
    /// graceful close flips this back to false before the next save.
    /// Replaces the legacy in-process RadioVisible field — kept for
    /// migration only; the radio companion now owns radio.json for its
    /// per-station / volume / viz settings.
    /// </summary>
    public bool RadioOpen { get; set; } = false;
    // Legacy fields — radio used to live in-process and these were the
    // widget's persisted state. The radio companion now owns radio.json;
    // these are kept so old saves still parse and aren't a hard breakage.
    public bool RadioVisible { get; set; } = false;
    public float RadioX { get; set; } = 80;
    public float RadioY { get; set; } = 80;
    public int RadioStationIdx { get; set; } = 0;
    public float RadioVolume { get; set; } = 0.6f;
    public int RadioVizMode { get; set; } = 0;

    /// <summary>Persisted retro theme name (matches RetroTheme.Name).</summary>
    public string RetroThemeName { get; set; } = "";

    /// <summary>Index into WorldTeeClassicActivity's MeshPalettes array.
    /// JSON key kept stable across the Fuji Golf → World Tee Classic
    /// rename so existing settings.json files don't lose this preference.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("FujiGolfPaletteIdx")]
    public int WorldTeeClassicPaletteIdx { get; set; } = 0;

    /// <summary>Pet costume — name of <see cref="Scenes.DesktopPet.Costumes.CostumeType"/>.</summary>
    public string Costume { get; set; } = "None";

    /// <summary>
    /// Whether the sibling-process World Tee Classic window was open the
    /// last time the host main loop refreshed this snapshot. The scene
    /// polls the child process per-frame so a graceful close (user clicked
    /// the X) flips this back to false before the next persisted save.
    /// JSON key kept stable across the rename for backward compat.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("FujiGolfOpen")]
    public bool WorldTeeClassicOpen { get; set; } = false;

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
