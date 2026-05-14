namespace MouseHouse.Scenes.Activities.Chess;

/// <summary>Difficulty tier shown alongside each lesson — also drives
/// chapter ordering inside the menu (beginners first).</summary>
public enum TrainingDifficulty { Beginner, Intermediate, Advanced }

/// <summary>
/// One lesson the Training mode steps the user through. A lesson is a
/// FEN position plus a forced solution in UCI move strings — the same
/// shape the offline puzzles already use, so the existing solver flow
/// (input gating + Show Move / Show Answer) works without changes.
///
/// <para><b>Description</b> is the reader-friendly explanation rendered
/// in the side info pane while in training mode (see DrawSidePanel
/// in RetroChessPuzzlesActivity). The status bar shows a short
/// synthesized prompt instead — the description text is too long for
/// the status row.</para>
/// </summary>
public record TrainingLesson(
    string Title,
    string Description,
    string Fen,
    string[] Solution,
    bool WhiteToMove,
    TrainingDifficulty Difficulty = TrainingDifficulty.Beginner);

/// <summary>One named group of related lessons — e.g. "Back-Rank Mates"
/// or "How the Knight Moves". Chapters are ordered roughly from easy
/// to hard.</summary>
public record TrainingChapter(string Id, string Name, TrainingLesson[] Lessons);

/// <summary>
/// All hand-authored lessons. New chapters can be appended to
/// <see cref="AllChapters"/> without touching the activity — the
/// chapter-picker menu is built from this list at runtime.
///
/// <para>The mate-pattern positions are mostly mate-in-one compositions
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
            new("Suffocation Mate",
                "A close cousin of smothered: the king is hemmed in by its own rook AND its own pawns. Knight to f7 is the only piece that can reach. Play Nf7#.",
                "6rk/6pp/8/4N3/8/8/4K3/8 w - - 0 1",
                new[] { "e5f7" }, WhiteToMove: true),
        }),

        // ── Edge-of-board knight + rook (Arabian / Anastasia) ────────
        new("edgemates", "Edge-of-Board Mates", new TrainingLesson[]
        {
            new("Anastasia's Mate",
                "Anastasia's pattern: a knight on e7 covers g6 and g8 while a rook swings to the h-file. The king on h7 has no escape — the knight pins it against the h-file and the rook delivers mate. Named for an 1803 novel by Wilhelm Heinse where the mate appears in a fictional game. Play Rh1#.",
                "8/4N1pk/8/8/8/8/4K3/3R4 w - - 0 1",
                new[] { "d1h1" }, WhiteToMove: true),
            new("Arabian Mate",
                "One of the oldest named mates — appears in 9th-century shatranj literature. The knight covers g7 from f5; the rook arrives on the 8th rank with no escape because the pawn on h7 walls in its own king. Play Ra8#.",
                "7k/7p/8/5N2/8/8/4K3/R7 w - - 0 1",
                new[] { "a1a8" }, WhiteToMove: true),
            new("Knight + Rook corner",
                "A variation of the Arabian pattern with the rook delivering from a different file. The knight on f6 covers g8 and h7; the rook arrives on g8 with check, and the king has no flight square. Play Rg8#.",
                "7k/7p/5N2/8/8/8/4K3/6R1 w - - 0 1",
                new[] { "g1g8" }, WhiteToMove: true),
        }),

        // ── Queen + king coordination ────────────────────────────────
        new("queenking", "Queen + King Mates", new TrainingLesson[]
        {
            new("Kiss of Death",
                "The fundamental queen mate: your king supports the queen exactly one square from the enemy king. The enemy king can neither capture (queen is defended) nor escape (queen covers every adjacent square). Play Qg7#.",
                "7k/8/6KQ/8/8/8/8/8 w - - 0 1",
                new[] { "h6g7" }, WhiteToMove: true),
            new("Damiano's Mate",
                "Named for Pedro Damiano (1512), one of the oldest published mate patterns. The black pawn on g7 walls in its own king, the white pawn on g6 supports the queen, and Qh7 delivers mate the king can't capture. Play Qh7#.",
                "7k/6p1/6P1/8/8/8/4K3/7Q w - - 0 1",
                new[] { "h1h7" }, WhiteToMove: true),
            new("Lolli's Mate",
                "Giambattista Lolli's 1763 pattern — queen + pawn against the corner. The white pawn on f6 supports the queen's invasion on g7; the king on h8 can't capture (queen defended), can't escape (rook on h-file blocks h-file via the pawn structure), and dies. Play Qxg7#.",
                "7k/5pp1/5P2/8/8/8/4K3/6Q1 w - - 0 1",
                new[] { "g1g7" }, WhiteToMove: true,
                TrainingDifficulty.Intermediate),
        }),

        // ── Bishop coordinations (Boden, Opera, Reti, Greco) ─────────
        new("bishops", "Bishop-Powered Mates", new TrainingLesson[]
        {
            new("Boden's Mate",
                "Samuel Boden's 1853 pattern: two bishops on crossing diagonals catch a castled king. The bishop on h3 covers the f1-c8 diagonal; the bishop arriving on a6 covers c8 itself. The king's own rooks pin him in place. Play Ba6#.",
                "1rkr4/2p5/8/8/8/7B/PP2B3/2K5 w - - 0 1",
                new[] { "e2a6" }, WhiteToMove: true,
                TrainingDifficulty.Intermediate),
            new("Greco's Mate",
                "Gioachino Greco (1620): queen + bishop on the long diagonal. The bishop on b1 reaches all the way to h7 along the light squares; the queen lands on h7 supported by the bishop; the king on h8 has no escape because its own pawn on g7 blocks g7 and the queen covers g8. Play Qxh7#.",
                "7k/6pp/8/7Q/8/8/8/1B2K3 w - - 0 1",
                new[] { "h5h7" }, WhiteToMove: true,
                TrainingDifficulty.Intermediate),
            new("Opera Mate",
                "Paul Morphy's 1858 finish at the Paris Opera. The queen on h4 covers the e7 escape, the rook arrives on d8 to deliver check, and the king's own pieces (rook on h8, pawns on f7-g7-h7) lock him in. Play Rd8#.",
                "4k2r/p4ppp/8/8/7Q/8/4K3/3R4 w - - 0 1",
                new[] { "d1d8" }, WhiteToMove: true),
            new("Reti's Mate",
                "Richard Reti's pattern: bishop covers an escape square along a long diagonal while a rook delivers on the back rank. The bishop on g5 covers e7; the rook on d8 is mate. Play Rd8#.",
                "4k3/p4ppp/8/6B1/8/8/4K3/3R4 w - - 0 1",
                new[] { "d1d8" }, WhiteToMove: true),
            new("Pillsbury's Mate",
                "Harry Pillsbury's variant: long-diagonal bishop on b2 controls the h8 escape square; the rook delivers the back-rank check on a8. Play Ra8#.",
                "6k1/5ppp/8/8/8/8/1B6/R3K3 w - - 0 1",
                new[] { "a1a8" }, WhiteToMove: true),
        }),

        // ── Geometric (Epaulette, Swallow's Tail) ────────────────────
        new("geometric", "Geometric Mates", new TrainingLesson[]
        {
            new("Epaulette Mate",
                "Named for the king's two rooks pinning its shoulders like military epaulettes. The king has no escape squares because its own rooks block d8 and f8; the queen on e7 is defended by your king on d6, so it can't be captured. Play Qe7#.",
                "3rkr2/8/3KQ3/8/8/8/8/8 w - - 0 1",
                new[] { "e6e7" }, WhiteToMove: true),
            new("Swallow's Tail (Guéridon)",
                "Also known as Guéridon Mate from the French for a small round table the position resembles. Pawns on d7 and f7 block diagonally — your king on f6 supports the queen for mate on e7. Play Qe7#.",
                "4k3/3p1p2/5K2/8/8/8/8/4Q3 w - - 0 1",
                new[] { "e1e7" }, WhiteToMove: true),
        }),

        // ── Opening-trap mates ───────────────────────────────────────
        new("opening_traps", "Opening-Trap Mates", new TrainingLesson[]
        {
            new("Scholar's Mate",
                "The four-move mate every beginner sees — and falls for — at some point. Your queen on f3 takes f7, supported by the bishop on c4. The weakest square in the opening (f7 / f2) defended only by the king. Play Qxf7#.",
                "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5Q2/PPPP1PPP/RNB1K1NR w KQkq - 0 3",
                new[] { "f3f7" }, WhiteToMove: true),
            new("Fool's Mate (Black to play)",
                "The fastest mate in chess — just two moves. White's f-pawn and g-pawn moves opened the e1-h4 diagonal, and black's queen ends it. The mistake every beginner makes once. Black plays …Qh4#.",
                "rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq - 0 2",
                new[] { "d8h4" }, WhiteToMove: false),
        }),

        // ── Endgame technique ────────────────────────────────────────
        new("endgames", "Basic Endgame Mates", new TrainingLesson[]
        {
            new("K + Q vs K",
                "King-and-queen against a lone king is always winning. Drive the enemy king to the edge with the queen, bring your king up for support, deliver the kiss-of-death. Your king on c6 supports the queen for mate. Play Qb7#.",
                "k7/8/2K5/8/8/8/8/1Q6 w - - 0 1",
                new[] { "b1b7" }, WhiteToMove: true),
            new("K + R vs K",
                "Rook endings need the king's help. Your king on b6 covers a7 and b7; the rook delivers mate along the 8th rank. Play Rh8#.",
                "k7/8/1K6/8/8/8/8/7R w - - 0 1",
                new[] { "h1h8" }, WhiteToMove: true),
            new("Two-rook ladder",
                "The classic staircase: one rook cuts off the 7th rank so the king can't drop down; the other rook delivers mate on the 8th. Works from any side of the board. Play Rh8#.",
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
