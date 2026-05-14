namespace MouseHouse.Scenes.Activities.Chess;

/// <summary>
/// One lesson the Training mode steps the user through. A lesson is a
/// FEN position plus a forced solution in UCI move strings — the same
/// shape the offline puzzles already use, so the existing solver flow
/// (input gating + Show Move / Show Answer) works without changes.
///
/// <para><b>Goal</b> is a one-sentence hint the activity shows above
/// the board (e.g. "Find checkmate in one — back-rank pattern"). The
/// title is shown in the chapter picker.</para>
/// </summary>
public record TrainingLesson(
    string Title,
    string Goal,
    string Fen,
    string[] Solution,
    bool WhiteToMove);

/// <summary>One named group of related lessons — e.g. "Back-Rank Mates"
/// or "How the Knight Moves". Chapters are ordered roughly from easy
/// to hard.</summary>
public record TrainingChapter(string Id, string Name, TrainingLesson[] Lessons);

/// <summary>
/// All hand-authored lessons. New chapters can be appended to
/// <see cref="AllChapters"/> without touching the activity — the
/// chapter-picker menu is built from this list at runtime.
///
/// <para>The mate-pattern positions are mate-in-one compositions
/// keyed to the named pattern they exemplify. Some are textbook
/// problems; others are simplified illustrations of the same motif
/// (knight + rook coordination, queen + king kiss-of-death, etc.)
/// so the user sees the geometry without having to memorise a
/// specific historical game.</para>
/// </summary>
public static class TrainingLibrary
{
    public static readonly TrainingChapter[] AllChapters =
    {
        // ── Back-rank patterns ───────────────────────────────────────
        new("backrank", "Back-Rank Mates", new TrainingLesson[]
        {
            new("Basic back rank",
                "The back rank is the row your king started on. When the king's own pawns block its escape, a rook on the back rank is mate. Play Ra8#.",
                "6k1/5ppp/8/8/8/8/8/R6K w - - 0 1",
                new[] { "a1a8" }, WhiteToMove: true),
            new("Queen on the back rank",
                "Same idea with a queen instead of a rook — the queen sweeps the whole row. Play Qe8#.",
                "6k1/5ppp/8/8/8/8/8/4Q2K w - - 0 1",
                new[] { "e1e8" }, WhiteToMove: true),
            new("Quiet rook lift",
                "The 7th-rank pawns lock the king on g8 — any rook reaching the 8th rank is mate. Play Re8#.",
                "6k1/5ppp/8/8/8/8/8/4R2K w - - 0 1",
                new[] { "e1e8" }, WhiteToMove: true),
        }),

        // ── Smothered + knight patterns ───────────────────────────────
        new("smothered", "Smothered Mates", new TrainingLesson[]
        {
            new("Classic smothered",
                "The king has nowhere to run — every escape square is filled by its own pieces. Only a knight can reach through. Play Nf7#.",
                "6rk/6pp/7N/8/8/8/4K3/8 w - - 0 1",
                new[] { "h6f7" }, WhiteToMove: true),
            new("Black smothered",
                "Smothered mates work from either side. The white king is locked in by its own rook and pawns. Black plays …Nf2#.",
                "4k3/8/8/8/6n1/8/6PP/6RK b - - 0 1",
                new[] { "g4f2" }, WhiteToMove: false),
        }),

        // ── Edge-of-board knight + rook (Arabian / Anastasia) ────────
        new("edgemates", "Edge-of-Board Mates", new TrainingLesson[]
        {
            new("Anastasia's Mate",
                "Knight on e7 covers g6 and g8; the rook swings to the h-file to finish. Play Rh1#.",
                "8/4N1pk/8/8/8/8/4K3/3R4 w - - 0 1",
                new[] { "d1h1" }, WhiteToMove: true),
            new("Arabian Mate",
                "The black pawn on h7 locks the king to h8, the knight on f5 covers g7 — the rook arrives on the 8th to mate. Play Ra8#.",
                "7k/7p/8/5N2/8/8/4K3/R7 w - - 0 1",
                new[] { "a1a8" }, WhiteToMove: true),
            new("Knight + Rook corner",
                "Knight covers g8 and the rook delivers mate on the 8th rank. Play Rg8#.",
                "7k/7p/5N2/8/8/8/4K3/6R1 w - - 0 1",
                new[] { "g1g8" }, WhiteToMove: true),
        }),

        // ── Queen + king coordination ────────────────────────────────
        new("queenking", "Queen + King Mates", new TrainingLesson[]
        {
            new("Kiss of Death",
                "The king supports the queen one square from the enemy king — no escape, no capture. Play Qg7#.",
                "7k/8/6KQ/8/8/8/8/8 w - - 0 1",
                new[] { "h6g7" }, WhiteToMove: true),
            new("Damiano-style",
                "Pawn jam in front of the king — your queen lands on h7 supported by your own pawn on g6. Play Qh7#.",
                "7k/6p1/6P1/8/8/8/4K3/7Q w - - 0 1",
                new[] { "h1h7" }, WhiteToMove: true),
        }),

        // ── Bishop coordinations (Boden, Opera) ──────────────────────
        new("bishops", "Bishop-Powered Mates", new TrainingLesson[]
        {
            new("Boden's Mate",
                "Two bishops on intersecting diagonals — the king has nowhere on the dark or light squares to flee. Play Ba6#.",
                "1rkr4/2p5/8/8/8/7B/PP2B3/2K5 w - - 0 1",
                new[] { "e2a6" }, WhiteToMove: true),
            new("Opera Mate",
                "Queen on h4 controls e7 and supports the rook on d8; the king's own rook and pawns seal it in. Morphy's famous Opera House finish — play Rd8#.",
                "4k2r/p4ppp/8/8/7Q/8/4K3/3R4 w - - 0 1",
                new[] { "d1d8" }, WhiteToMove: true),
            new("Reti's Mate",
                "Bishop on g5 covers the e7 escape and defends the d8 square; the rook arrives on d8 for the kill. Play Rd8#.",
                "4k3/p4ppp/8/6B1/8/8/4K3/3R4 w - - 0 1",
                new[] { "d1d8" }, WhiteToMove: true),
            new("Pillsbury's Mate",
                "Long-diagonal bishop on b2 covers h8; rook delivers the back-rank check. Play Ra8#.",
                "6k1/5ppp/8/8/8/8/1B6/R3K3 w - - 0 1",
                new[] { "a1a8" }, WhiteToMove: true),
        }),

        // ── Geometric (Epaulette, Swallow's Tail) ────────────────────
        new("geometric", "Geometric Mates", new TrainingLesson[]
        {
            new("Epaulette Mate",
                "The king's own rooks pin its shoulders — only one square is attacked, and the queen takes it (defended by your king). Play Qe7#.",
                "3rkr2/8/3KQ3/8/8/8/8/8 w - - 0 1",
                new[] { "e6e7" }, WhiteToMove: true),
            new("Swallow's Tail (Guéridon)",
                "Pawns on d7 and f7 block diagonally — your king on f6 supports the queen for mate on e7. Play Qe7#.",
                "4k3/3p1p2/5K2/8/8/8/8/4Q3 w - - 0 1",
                new[] { "e1e7" }, WhiteToMove: true),
        }),

        // ── Opening-trap mates ───────────────────────────────────────
        new("opening_traps", "Opening-Trap Mates", new TrainingLesson[]
        {
            new("Scholar's Mate",
                "The four-move mate every beginner has seen. Your queen on f3 takes f7, supported by the bishop on c4. Play Qxf7#.",
                "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5Q2/PPPP1PPP/RNB1K1NR w KQkq - 0 3",
                new[] { "f3f7" }, WhiteToMove: true),
            new("Fool's Mate (Black to play)",
                "The fastest mate in chess — two moves. White's pawn moves opened the e1-h4 diagonal. Black plays …Qh4#.",
                "rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq - 0 2",
                new[] { "d8h4" }, WhiteToMove: false),
        }),

        // ── Endgame technique ────────────────────────────────────────
        new("endgames", "Basic Endgame Mates", new TrainingLesson[]
        {
            new("K + Q vs K",
                "King-and-queen against a lone king is always winning. Your king on c6 supports the queen for mate. Play Qb7#.",
                "k7/8/2K5/8/8/8/8/1Q6 w - - 0 1",
                new[] { "b1b7" }, WhiteToMove: true),
            new("K + R vs K",
                "Your king on b6 covers a7 and b7; the rook delivers mate along the 8th rank. Play Rh8#.",
                "k7/8/1K6/8/8/8/8/7R w - - 0 1",
                new[] { "h1h8" }, WhiteToMove: true),
            new("Two-rook ladder",
                "The classic staircase. One rook cuts off the 7th rank; the other delivers mate on the 8th. Play Rh8#.",
                "k7/6R1/8/8/8/8/7R/7K w - - 0 1",
                new[] { "h2h8" }, WhiteToMove: true),
        }),
    };

    /// <summary>Total lesson count across all chapters — handy for a
    /// progress label like "12/35" in the status bar.</summary>
    public static int TotalLessonCount
    {
        get
        {
            int n = 0;
            foreach (var c in AllChapters) n += c.Lessons.Length;
            return n;
        }
    }
}
