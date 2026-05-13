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
    /// <summary>"golf" or "chess". Tagged so a single matches.json
    /// file can hold either kind and a future stats UI can split
    /// them by category without scanning every field.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "golf";

    [JsonPropertyName("ended_at")] public DateTime EndedAtUtc { get; set; }
    [JsonPropertyName("peer_code")] public string PeerCode { get; set; } = "";
    [JsonPropertyName("peer_name")] public string PeerName { get; set; } = "";

    // ── Golf-specific (kind == "golf") ────────────────────────────────
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

    // ── Chess-specific (kind == "chess") ──────────────────────────────
    [JsonPropertyName("time_limit_seconds")] public int TimeLimitSeconds { get; set; }
    [JsonPropertyName("starting_band")] public string StartingBand { get; set; } = "";
    [JsonPropertyName("puzzle_count")] public int PuzzleCount { get; set; }
    [JsonPropertyName("local_solved")] public int LocalSolved { get; set; }
    [JsonPropertyName("peer_solved")] public int PeerSolved { get; set; }
    /// <summary>Wall-clock ms from match start to when the local
    /// player solved their last puzzle. Tiebreaker when
    /// LocalSolved == PeerSolved.</summary>
    [JsonPropertyName("local_last_solve_ms")] public long LocalLastSolveMs { get; set; }
    [JsonPropertyName("peer_last_solve_ms")] public long PeerLastSolveMs { get; set; }

    // ── Tetris-specific (kind == "tetris") ────────────────────────────
    [JsonPropertyName("starting_level")] public int StartingLevel { get; set; }
    [JsonPropertyName("duration_ms")] public long DurationMs { get; set; }
    [JsonPropertyName("local_score")] public int LocalScore { get; set; }
    [JsonPropertyName("local_lines")] public int LocalLines { get; set; }
    [JsonPropertyName("local_final_level")] public int LocalFinalLevel { get; set; }
    [JsonPropertyName("peer_score")] public int PeerScore { get; set; }
    [JsonPropertyName("peer_lines")] public int PeerLines { get; set; }
    [JsonPropertyName("peer_final_level")] public int PeerFinalLevel { get; set; }
    /// <summary>"local" or "peer" — who topped out / forfeited first.
    /// The other side won. Empty when the match ended without a
    /// resolved top-out (e.g. both sides closed the window).</summary>
    [JsonPropertyName("loser")] public string Loser { get; set; } = "";

    // ── Hearts-specific (kind == "hearts") ────────────────────────────
    /// <summary>Display names for all 4 seats, ordered seat 0..3.
    /// Seat 0 is the local player (the "you" perspective is per-
    /// client; both host's and shadow's records use their own
    /// local 0). AI seats use "Computer N" style names.</summary>
    [JsonPropertyName("hearts_seats")] public List<string> HeartsSeats { get; set; } = new();
    /// <summary>Final scores per seat, same ordering as HeartsSeats.</summary>
    [JsonPropertyName("hearts_final_scores")] public List<int> HeartsFinalScores { get; set; } = new();
    /// <summary>Per-seat moon-shot count over the match. Same ordering.</summary>
    [JsonPropertyName("hearts_moon_shots")] public List<int> HeartsMoonShots { get; set; } = new();
    /// <summary>Seat index of the winner (lowest score). -1 if
    /// the match ended without a resolution.</summary>
    [JsonPropertyName("hearts_winner_seat")] public int HeartsWinnerSeat { get; set; } = -1;
    /// <summary>This client's seat index in the match. Used to
    /// compute LocalWon for Kind=="hearts".</summary>
    [JsonPropertyName("hearts_local_seat")] public int HeartsLocalSeat { get; set; }

    /// <summary>Convenience: true if the local player won. Rules:
    /// golf → finished first by wall-clock; chess → more solved
    /// (tiebreak: faster last-solve). Ties resolve as false in both
    /// games.</summary>
    [JsonIgnore]
    public bool LocalWon
    {
        get
        {
            if (Kind == "chess")
            {
                if (LocalSolved != PeerSolved) return LocalSolved > PeerSolved;
                // Tiebreak: faster last-solve wins. Zero on either
                // side means "never solved any" — that side loses
                // the tiebreak.
                if (LocalLastSolveMs == 0) return false;
                if (PeerLastSolveMs == 0) return true;
                return LocalLastSolveMs < PeerLastSolveMs;
            }
            if (Kind == "tetris")
            {
                // Loser-tagged: whichever side topped out / forfeited
                // first lost. Empty Loser means inconclusive end
                // (both windows closed before either top-out) — no
                // win for either.
                return Loser == "peer";
            }
            if (Kind == "hearts")
            {
                // Lowest final score wins; ties resolve as false
                // (Hearts has no fundamental tie-breaker here).
                if (HeartsWinnerSeat < 0) return false;
                return HeartsLocalSeat == HeartsWinnerSeat;
            }
            // Default: golf.
            return LocalFinished
                && (!PeerFinished || LocalFinishMs < PeerFinishMs);
        }
    }
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

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MatchRecord is a small fixed POCO; reflection-based JSON is intentional.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Same — small POCO, JSON shape is stable.")]
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

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MatchRecord is a small fixed POCO; reflection-based JSON is intentional.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Same — small POCO, JSON shape is stable.")]
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
