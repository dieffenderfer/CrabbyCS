using System.Text.Json;
using System.Text.Json.Serialization;
using MouseHouse.Core;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// One completed (or abandoned) netplay-golf race. Stored in
/// matches.json so the buddy list can show a tiny "last match"
/// hint per friend and so a future stats panel has data to work
/// against. Capped to the last <see cref="MaxKept"/> rows on each
/// save so the file doesn't grow unbounded.
/// </summary>
public sealed class MatchRecord
{
    [JsonPropertyName("ended_at")] public DateTime EndedAtUtc { get; set; }
    [JsonPropertyName("peer_code")] public string PeerCode { get; set; } = "";
    [JsonPropertyName("peer_name")] public string PeerName { get; set; } = "";
    [JsonPropertyName("region")] public string Region { get; set; } = "";
    [JsonPropertyName("difficulty")] public int Difficulty { get; set; }
    [JsonPropertyName("seed")] public int Seed { get; set; }
    [JsonPropertyName("local_strokes")] public int LocalStrokes { get; set; }
    [JsonPropertyName("local_finish_ms")] public long LocalFinishMs { get; set; }
    [JsonPropertyName("local_finished")] public bool LocalFinished { get; set; }
    [JsonPropertyName("peer_strokes")] public int PeerStrokes { get; set; }
    [JsonPropertyName("peer_finish_ms")] public long PeerFinishMs { get; set; }
    [JsonPropertyName("peer_finished")] public bool PeerFinished { get; set; }
    [JsonPropertyName("peer_disconnected")] public bool PeerDisconnected { get; set; }

    /// <summary>Convenience: true if the local player won (finished
    /// first by wall-clock). Tied → false; abandoned by peer → true
    /// only if local finished.</summary>
    [JsonIgnore]
    public bool LocalWon
        => LocalFinished
        && (!PeerFinished || LocalFinishMs < PeerFinishMs);
}

/// <summary>
/// Persistent ring-buffer of recent match records. Each call to
/// <see cref="Append"/> writes the file; capped at <see cref="MaxKept"/>
/// rows so older matches drop off naturally.
/// </summary>
public static class MatchHistory
{
    public const string Filename = "matches.json";
    public const int MaxKept = 50;

    public static List<MatchRecord> Load()
    {
        try
        {
            var path = Path.Combine(SaveManager.SaveDirectory, Filename);
            if (!File.Exists(path)) return new();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<MatchRecord>>(json) ?? new();
        }
        catch { return new(); }
    }

    public static void Append(MatchRecord r)
    {
        try
        {
            var list = Load();
            list.Add(r);
            // Drop the oldest until we're back under the cap.
            while (list.Count > MaxKept) list.RemoveAt(0);
            Directory.CreateDirectory(SaveManager.SaveDirectory);
            var path = Path.Combine(SaveManager.SaveDirectory, Filename);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(list, opts));
        }
        catch { /* best-effort */ }
    }
}
