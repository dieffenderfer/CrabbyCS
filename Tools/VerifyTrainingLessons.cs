using System;
using MinimalChess;
using MouseHouse.Scenes.Activities.Chess;

namespace MouseHouse.Tools;

/// <summary>
/// One-shot verifier: load every training lesson, play the solution
/// move in MinimalChess.Board, and assert the resulting position is
/// checkmate. Run with:
///     dotnet run -c Debug -p:DefineConstants=TRAINING_VERIFY
/// Outputs one line per lesson — "OK" or "BAD <reason>" — and exits
/// with a non-zero status if any lesson is broken so it can be wired
/// into CI later if we want.
/// </summary>
public static class VerifyTrainingLessons
{
    public static int Run()
    {
        int bad = 0;
        foreach (var ch in TrainingLibrary.AllChapters)
        {
            foreach (var lesson in ch.Lessons)
            {
                string status = CheckLesson(lesson);
                Console.WriteLine($"[{ch.Id}] {lesson.Title}: {status}");
                if (!status.StartsWith("OK")) bad++;
            }
        }
        Console.WriteLine($"\n{bad} broken lesson(s).");
        return bad == 0 ? 0 : 1;
    }

    private static string CheckLesson(TrainingLesson lesson)
    {
        Board board;
        try { board = new Board(lesson.Fen); }
        catch (Exception ex) { return $"BAD (FEN parse: {ex.Message})"; }

        if (lesson.Solution.Length == 0)
            return "BAD (no solution)";

        // Mate-in-N lessons: play the whole sequence, alternating sides
        // (the user plays odd plies, the opponent plays even). For a
        // mate-in-1 the solution is one move and the resulting
        // position must be checkmate.
        for (int i = 0; i < lesson.Solution.Length; i++)
        {
            Move move;
            try { move = new Move(lesson.Solution[i]); }
            catch (Exception ex) { return $"BAD (move parse '{lesson.Solution[i]}': {ex.Message})"; }

            if (!board.IsPlayable(move))
                return $"BAD (move {lesson.Solution[i]} not playable from position)";

            board.Play(move);
        }

        // After the last move, the side-to-move (the opponent) should
        // be in check AND have no LEGAL replies — i.e. checkmate.
        // MinimalChess.CollectMoves returns pseudo-legal moves (it
        // includes moves that leave the mover in check) — filter them
        // by trial-playing each and checking the resulting position.
        bool inCheck = board.IsChecked(board.SideToMove);
        var stm = board.SideToMove;
        var pseudo = new System.Collections.Generic.List<Move>();
        board.CollectMoves(m => pseudo.Add(m));
        var legalReplies = new System.Collections.Generic.List<string>();
        foreach (var m in pseudo)
        {
            var copy = new Board(board, m);
            if (!copy.IsChecked(stm))
                legalReplies.Add(m.ToString());
        }
        if (!inCheck) return $"BAD (final position is not check; STM={stm})";
        if (legalReplies.Count > 0) return $"BAD (escape: {string.Join(",", legalReplies)})";
        return "OK";
    }
}
