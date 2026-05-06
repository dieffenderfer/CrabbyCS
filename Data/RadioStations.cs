namespace MouseHouse.Data;

/// <summary>
/// One station entry. <see cref="Slug"/> is the SomaFM channel id used to
/// fetch the now-playing JSON; leave blank for non-SomaFM streams.
/// </summary>
public record class RadioStation(string Name, string Url, string Genre, string Slug = "");

/// <summary>
/// Bundled list of publicly-broadcast internet streams. SomaFM channels are
/// listener-supported and intentionally open to public streaming. Drop a
/// stations.json file in the app's save dir to override / extend this list.
/// </summary>
public static class RadioStations
{
    public static readonly IReadOnlyList<RadioStation> Defaults = new[]
    {
        new RadioStation("Groove Salad",     "http://ice1.somafm.com/groovesalad-128-mp3",   "ambient",    "groovesalad"),
        new RadioStation("Drone Zone",       "http://ice1.somafm.com/dronezone-128-mp3",     "ambient",    "dronezone"),
        new RadioStation("Indie Pop Rocks",  "http://ice1.somafm.com/indiepop-128-mp3",      "indie",      "indiepop"),
        new RadioStation("Secret Agent",     "http://ice1.somafm.com/secretagent-128-mp3",   "lounge",     "secretagent"),
        new RadioStation("Lush",             "http://ice1.somafm.com/lush-128-mp3",          "vocals",     "lush"),
        new RadioStation("Boot Liquor",      "http://ice1.somafm.com/bootliquor-128-mp3",    "americana",  "bootliquor"),
        new RadioStation("Beat Blender",     "http://ice1.somafm.com/beatblender-128-mp3",   "electronic", "beatblender"),
        new RadioStation("Deep Space One",   "http://ice1.somafm.com/deepspaceone-128-mp3",  "ambient",    "deepspaceone"),
        new RadioStation("Mission Control",  "http://ice1.somafm.com/missioncontrol-128-mp3","space",      "missioncontrol"),
        new RadioStation("DEF CON Radio",    "http://ice1.somafm.com/defcon-128-mp3",        "electronic", "defcon"),
        new RadioStation("WCPE Classical",   "https://audio-mp3.ibiblio.org/wcpe.mp3",       "classical"),
    };

    private static IReadOnlyList<RadioStation>? _all;

    /// <summary>Returns the bundled list, optionally extended by a user JSON file.</summary>
    public static IReadOnlyList<RadioStation> All => _all ??= LoadAll();

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "RadioStation is a small fixed POCO; reflection-based deserialisation is intentional.")]
    private static IReadOnlyList<RadioStation> LoadAll()
    {
        // Allow user customisation via settings dir / stations.json
        var path = Path.Combine(Core.SaveManager.SaveDirectory, "stations.json");
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var arr = System.Text.Json.JsonSerializer.Deserialize<List<RadioStation>>(json);
                if (arr != null && arr.Count > 0) return arr;
            }
            catch { /* fall through to defaults */ }
        }
        return Defaults;
    }
}
