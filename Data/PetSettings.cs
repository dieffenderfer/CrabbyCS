using MouseHouse.Core;

namespace MouseHouse.Data;

/// <summary>
/// Persisted pet preferences: color mode, scale, mute state.
/// </summary>
public class PetSettings
{
    public string ColorMode { get; set; } = "2color"; // "2color", "1color", "fullcolor"
    public int ScaleOverride { get; set; } = 0; // 0 = default
    public bool Muted { get; set; } = false;
    public bool PinkNose { get; set; } = false;

    private const string Filename = "settings.json";

    public static PetSettings Load()
    {
        return SaveManager.LoadOrDefault<PetSettings>(Filename);
    }

    public void Save()
    {
        SaveManager.Save(Filename, this);
    }
}
