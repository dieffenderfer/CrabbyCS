using MouseHouse.Core;

namespace MouseHouse;

public static class Program
{
    public static int Main(string[] args)
    {
        // One-shot maintenance modes — must run before the graphics
        // window opens. The training-lesson verifier walks the entire
        // TrainingLibrary, plays each lesson's solution in the
        // vendored MinimalChess engine, and asserts the final
        // position is checkmate. Useful for catching FEN typos
        // before they hit users.
        if (args.Length > 0 && args[0] == "--verify-training")
            return MouseHouse.Tools.VerifyTrainingLessons.Run();

        try
        {
            var app = new App();
            app.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CRASH: {ex}");
            throw;
        }
        return 0;
    }
}
