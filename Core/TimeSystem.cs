using Raylib_cs;

namespace MouseHouse.Core;

public enum TimeOfDay { Dawn, Day, Dusk, Night }
public enum Season { Spring, Summer, Fall, Winter }

/// <summary>
/// Tracks real-world time for day/night cycle and seasonal events.
/// </summary>
public static class TimeSystem
{
    public static TimeOfDay CurrentTimeOfDay { get; private set; }
    public static Season CurrentSeason { get; private set; }

    // Overlay color for day/night tinting (applied over the whole screen)
    public static Color OverlayColor { get; private set; }

    private static readonly Color DawnColor = new(255, 200, 150, 15);
    private static readonly Color DayColor = new(0, 0, 0, 0);
    private static readonly Color DuskColor = new(255, 140, 80, 25);
    private static readonly Color NightColor = new(20, 20, 60, 40);

    public static void Update()
    {
        var now = DateTime.Now;
        int hour = now.Hour;

        CurrentTimeOfDay = hour switch
        {
            >= 5 and < 7 => TimeOfDay.Dawn,
            >= 7 and < 17 => TimeOfDay.Day,
            >= 17 and < 19 => TimeOfDay.Dusk,
            _ => TimeOfDay.Night
        };

        OverlayColor = CurrentTimeOfDay switch
        {
            TimeOfDay.Dawn => DawnColor,
            TimeOfDay.Day => DayColor,
            TimeOfDay.Dusk => DuskColor,
            TimeOfDay.Night => NightColor,
            _ => DayColor
        };

        int month = now.Month;
        CurrentSeason = month switch
        {
            >= 3 and <= 5 => Season.Spring,
            >= 6 and <= 8 => Season.Summer,
            >= 9 and <= 11 => Season.Fall,
            _ => Season.Winter
        };
    }

    public static bool IsHoliday(string name)
    {
        var now = DateTime.Now;
        return name.ToLower() switch
        {
            "valentine" => now.Month == 2 && now.Day >= 10 && now.Day <= 14,
            "stpatrick" => now.Month == 3 && now.Day >= 14 && now.Day <= 17,
            "easter" => now.Month == 3 || now.Month == 4, // simplified
            "halloween" => now.Month == 10 && now.Day >= 25,
            "thanksgiving" => now.Month == 11 && now.Day >= 20 && now.Day <= 28,
            "christmas" => now.Month == 12 && now.Day >= 20,
            "newyear" => now.Month == 1 && now.Day <= 3,
            _ => false
        };
    }
}
