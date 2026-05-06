namespace MouseHouse.Data;

public record class RadioStation(string Name, string Url, string Genre);

/// <summary>
/// Bundled list of publicly-broadcast internet streams. SomaFM channels are
/// listener-supported and intentionally open to public streaming. Drop a
/// stations.json file in the app's save dir to override / extend this list.
/// </summary>
public static class RadioStations
{
    public static readonly IReadOnlyList<RadioStation> Defaults = new[]
    {
        new RadioStation("Groove Salad",     "http://ice1.somafm.com/groovesalad-128-mp3",   "ambient"),
        new RadioStation("Drone Zone",       "http://ice1.somafm.com/dronezone-128-mp3",     "ambient"),
        new RadioStation("Indie Pop Rocks",  "http://ice1.somafm.com/indiepop-128-mp3",      "indie"),
        new RadioStation("Secret Agent",     "http://ice1.somafm.com/secretagent-128-mp3",   "lounge"),
        new RadioStation("Lush",             "http://ice1.somafm.com/lush-128-mp3",          "vocals"),
        new RadioStation("Boot Liquor",      "http://ice1.somafm.com/bootliquor-128-mp3",    "americana"),
        new RadioStation("Beat Blender",     "http://ice1.somafm.com/beatblender-128-mp3",   "electronic"),
        new RadioStation("Deep Space One",   "http://ice1.somafm.com/deepspaceone-128-mp3",  "ambient"),
        new RadioStation("Mission Control",  "http://ice1.somafm.com/missioncontrol-128-mp3","space"),
        new RadioStation("DEF CON Radio",    "http://ice1.somafm.com/defcon-128-mp3",        "electronic"),
    };

    private static IReadOnlyList<RadioStation>? _all;

    /// <summary>Returns the bundled list, optionally extended by a user JSON file.</summary>
    public static IReadOnlyList<RadioStation> All => _all ??= LoadAll();

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
