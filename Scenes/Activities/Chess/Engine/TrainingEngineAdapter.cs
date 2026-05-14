using MinimalChess;

namespace MouseHouse.Scenes.Activities.Chess.Engine;

/// <summary>
/// Thin wrapper around the vendored MinimalChess search so the rest of
/// the codebase doesn't need to know about MinimalChess types. Each
/// call to <see cref="ChooseMove"/> creates a fresh Board from the
/// supplied FEN, runs an iterative deepening search to a depth-capped
/// limit, and returns the engine's best move as a UCI string (e.g.
/// "e2e4", "g7g8q").
///
/// Strength is controlled by <see cref="LevelDepth"/> — a tiny mapping
/// from a 1..5 "training level" to a search depth in plies. Depth 1
/// plays randomly-ish (knight-fork blunders allowed); depth 5 plays
/// well over 2000 Elo. Most lessons that ask the engine to "play it
/// out" want the easier side of that range — the lesson should feel
/// like learning, not like getting crushed by a 2400 engine.
/// </summary>
public static class TrainingEngineAdapter
{
    public static int LevelDepth(int level) => level switch
    {
        <= 1 => 1,
        2 => 2,
        3 => 3,
        4 => 4,
        _ => 5,
    };

    /// <summary>
    /// Search the position at <paramref name="fen"/> and return the
    /// engine's best move in UCI notation. Returns null on a terminal
    /// position (no legal move available — checkmate or stalemate).
    /// </summary>
    public static string? ChooseMove(string fen, int level)
    {
        var board = new Board(fen);
        // Quick legality probe — IterativeSearch on a terminal position
        // returns an empty PV, which we want to surface as null so the
        // caller can decide whether to declare mate/stalemate.
        bool anyMove = false;
        board.CollectMoves(_ => anyMove = true);
        if (!anyMove) return null;

        int depth = LevelDepth(level);
        var search = new IterativeSearch(depth, board);
        if (search.PrincipalVariation.Length == 0) return null;
        return search.PrincipalVariation[0].ToString();
    }
}
