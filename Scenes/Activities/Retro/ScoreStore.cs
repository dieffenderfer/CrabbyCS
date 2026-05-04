using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities.Retro;

public class ScoreEntry
{
    public string Name { get; set; } = "";
    public int Score { get; set; }
    public long TimestampUnix { get; set; }
}

public class GameScores
{
    public Dictionary<string, List<ScoreEntry>> ByDifficulty { get; set; } = new();
}

/// <summary>
/// Per-game high-score persistence. Each game id gets its own JSON file under
/// the platform save dir. "difficulty" is a free-form key (e.g. "beginner",
/// "default") so games with one mode can just use "default".
/// </summary>
public static class ScoreStore
{
    private const int MaxEntries = 10;

    public static List<ScoreEntry> Get(string gameId, string difficulty = "default")
    {
        var data = SaveManager.LoadOrDefault<GameScores>(FileName(gameId));
        return data.ByDifficulty.TryGetValue(difficulty, out var list)
            ? list
            : new List<ScoreEntry>();
    }

    /// <summary>Adds an entry, sorts (higher is better), trims, persists. Returns the rank (1-based) or 0 if not in top N.</summary>
    public static int Submit(string gameId, string difficulty, string name, int score, bool higherIsBetter = true)
    {
        var data = SaveManager.LoadOrDefault<GameScores>(FileName(gameId));
        if (!data.ByDifficulty.TryGetValue(difficulty, out var list))
        {
            list = new List<ScoreEntry>();
            data.ByDifficulty[difficulty] = list;
        }

        var entry = new ScoreEntry
        {
            Name = name,
            Score = score,
            TimestampUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        list.Add(entry);

        if (higherIsBetter) list.Sort((a, b) => b.Score.CompareTo(a.Score));
        else                list.Sort((a, b) => a.Score.CompareTo(b.Score));

        if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);

        SaveManager.Save(FileName(gameId), data);

        int rank = list.IndexOf(entry) + 1;
        return rank;
    }

    private static string FileName(string gameId) => $"scores_{gameId}.json";
}
