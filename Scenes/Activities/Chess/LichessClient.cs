using System.Net.Http;
using System.Text.Json;

namespace MouseHouse.Scenes.Activities.Chess;

/// <summary>
/// One puzzle as Lichess returns it: a starting-position PGN that we replay
/// from the standard opening to reach the puzzle position, plus the expected
/// solution as a list of UCI moves (the player plays the odd-indexed moves
/// after replay, the engine animates the even-indexed opponent replies),
/// rating, puzzle id, and themes ("mateIn1", "fork", "pin", "endgame", etc.).
/// </summary>
public record LichessPuzzle(string Pgn, string[] Solution, int Rating, string Id, string[] Themes);

public static class LichessClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    public const string Endpoint = "https://lichess.org/api/puzzle/next";

    public sealed class FetchResult
    {
        public LichessPuzzle? Puzzle;
        public string Error = "";
        public bool Ok => Puzzle != null && string.IsNullOrEmpty(Error);
    }

    public static async Task<FetchResult> FetchNextAsync()
    {
        var result = new FetchResult();
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Add("Accept", "application/json");
            var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var puzzle = root.GetProperty("puzzle");
            var game = root.GetProperty("game");

            var pgn = game.GetProperty("pgn").GetString() ?? "";
            var rating = puzzle.GetProperty("rating").GetInt32();
            var id = puzzle.GetProperty("id").GetString() ?? "";

            var solutionArr = puzzle.GetProperty("solution");
            var solution = new string[solutionArr.GetArrayLength()];
            for (int i = 0; i < solution.Length; i++) solution[i] = solutionArr[i].GetString()!;

            var themes = Array.Empty<string>();
            if (puzzle.TryGetProperty("themes", out var themesEl) && themesEl.ValueKind == JsonValueKind.Array)
            {
                themes = new string[themesEl.GetArrayLength()];
                for (int i = 0; i < themes.Length; i++) themes[i] = themesEl[i].GetString() ?? "";
            }

            if (solution.Length == 0 || pgn == "") { result.Error = "Empty puzzle from server"; return result; }
            result.Puzzle = new LichessPuzzle(pgn, solution, rating, id, themes);
            return result;
        }
        catch (HttpRequestException) { result.Error = "No connection"; return result; }
        catch (TaskCanceledException) { result.Error = "Request timed out"; return result; }
        catch (Exception ex) { result.Error = "Error: " + ex.Message; return result; }
    }

    /// <summary>
    /// Apply a Lichess PGN to the engine, replaying every move from the standard
    /// starting position. Lichess includes the opponent's setup move as the last
    /// PGN move, so after replay it's the player's turn to find solution[0].
    /// Returns false if any move in the PGN failed to apply.
    /// </summary>
    public static bool ApplyPuzzle(ChessEngine engine, LichessPuzzle p)
    {
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        engine.CastleWK = engine.CastleWQ = engine.CastleBK = engine.CastleBQ = true;
        engine.LastMoveFrom = (-1, -1);
        engine.LastMoveTo = (-1, -1);

        foreach (var san in ChessEngine.ParsePgnMoves(p.Pgn))
            if (!engine.ApplySanMove(san)) return false;
        return true;
    }

    /// <summary>
    /// Format a Lichess theme tag ("mateIn1", "knightEndgame") into a human
    /// label ("Mate in 1", "Knight endgame"). Pure mechanical camelCase split.
    /// </summary>
    public static string PrettifyTheme(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return "";
        var sb = new System.Text.StringBuilder(tag.Length + 4);
        for (int i = 0; i < tag.Length; i++)
        {
            char c = tag[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(tag[i - 1]) || (i + 1 < tag.Length && char.IsLower(tag[i + 1]))))
            {
                sb.Append(' ');
                sb.Append(char.ToLower(c));
            }
            else if (i == 0) sb.Append(char.ToUpper(c));
            else sb.Append(c);
        }
        // Numeric suffixes like "mateIn3" become "Mate in 3" naturally because
        // digits aren't upper/lower.
        return sb.ToString();
    }

    public static string FormatThemes(string[] themes, int max = 3)
    {
        if (themes == null || themes.Length == 0) return "";
        var picked = themes.Take(max).Select(PrettifyTheme).Where(s => s.Length > 0);
        return string.Join(" · ", picked);
    }
}
