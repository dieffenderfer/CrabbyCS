using MouseHouse.Core;

namespace MouseHouse;

public static class Program
{
    public static void Main(string[] args)
    {
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
    }
}
