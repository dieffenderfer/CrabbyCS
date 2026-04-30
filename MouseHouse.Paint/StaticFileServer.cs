using System.Net;

namespace MouseHouse.Paint;

internal sealed class StaticFileServer
{
    private readonly string _root;
    private readonly int _port;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;

    public StaticFileServer(string root, int port)
    {
        _root = Path.GetFullPath(root);
        _port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var rel = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath.TrimStart('/'));
            if (string.IsNullOrEmpty(rel)) rel = "index.html";

            var path = Path.GetFullPath(Path.Combine(_root, rel));
            if (!path.StartsWith(_root, StringComparison.Ordinal) || !File.Exists(path))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            ctx.Response.ContentType = MimeFor(Path.GetExtension(path));
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            using var fs = File.OpenRead(path);
            ctx.Response.ContentLength64 = fs.Length;
            fs.CopyTo(ctx.Response.OutputStream);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private static string MimeFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs"   => "application/javascript; charset=utf-8",
        ".css"            => "text/css; charset=utf-8",
        ".json"           => "application/json; charset=utf-8",
        ".webmanifest"    => "application/manifest+json; charset=utf-8",
        ".svg"            => "image/svg+xml",
        ".png"            => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif"            => "image/gif",
        ".ico"            => "image/x-icon",
        ".webp"           => "image/webp",
        ".wav"            => "audio/wav",
        ".mp3"            => "audio/mpeg",
        ".ogg"            => "audio/ogg",
        ".woff"           => "font/woff",
        ".woff2"          => "font/woff2",
        ".ttf"            => "font/ttf",
        ".otf"            => "font/otf",
        ".txt" or ".md"   => "text/plain; charset=utf-8",
        ".xml"            => "application/xml; charset=utf-8",
        _                 => "application/octet-stream",
    };
}
