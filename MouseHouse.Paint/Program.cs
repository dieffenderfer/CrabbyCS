using System.Net;
using System.Net.Sockets;
using Photino.NET;

namespace MouseHouse.Paint;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var rootDir = ResolveJsPaintRoot(args);
            if (rootDir is null)
            {
                Console.Error.WriteLine("[MouseHouse.Paint] could not locate bundled jspaint directory");
                return 2;
            }

            var port = PickFreePort();
            var server = new StaticFileServer(rootDir, port);
            server.Start();

            var url = $"http://127.0.0.1:{port}/index.html";

            var window = new PhotinoWindow()
                .SetTitle("Paint — MouseHouse")
                .SetUseOsDefaultSize(false)
                .SetSize(1200, 800)
                .SetResizable(true)
                .Center()
                .Load(url);

            window.WaitForClose();
            server.Stop();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MouseHouse.Paint] {ex}");
            return 1;
        }
    }

    private static string? ResolveJsPaintRoot(string[] args)
    {
        if (args.Length > 0 && Directory.Exists(args[0]))
            return args[0];

        var bundled = Path.Combine(AppContext.BaseDirectory, "jspaint");
        if (File.Exists(Path.Combine(bundled, "index.html")))
            return bundled;

        return null;
    }

    private static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
