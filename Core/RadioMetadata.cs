using System.Text;
using System.Text.Json;

namespace MouseHouse.Core;

/// <summary>
/// Now-playing fetcher for the radio widget. Two paths:
///   • SomaFM has a per-channel JSON endpoint with separate artist/title
///     fields — preferred when a slug is set.
///   • For everything else (e.g. WCPE Classical) we read inline ICY
///     (Icecast/Shoutcast) metadata directly from the stream URL: open a
///     short-lived HTTP request with `Icy-MetaData: 1`, skip one audio
///     block, decode the StreamTitle, close. Works for any Icecast/
///     Shoutcast station that publishes metadata.
/// Failures fall back to the last known values silently.
/// </summary>
public class RadioMetadata
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    public string CurrentArtist { get; private set; } = "";
    public string CurrentTitle { get; private set; } = "";

    private string? _slug;
    private string? _streamUrl;
    private DateTime _nextPoll = DateTime.MinValue;
    private Task? _inflight;

    public bool HasTrack => !string.IsNullOrEmpty(CurrentTitle);

    /// <summary>
    /// Pick the metadata source for the current station. <paramref name="slug"/>
    /// (when non-empty) wins — that's the SomaFM JSON path. Otherwise we use
    /// the stream URL for ICY metadata.
    /// </summary>
    public void SetSource(string? slug, string? streamUrl)
    {
        string? newSlug = string.IsNullOrEmpty(slug) ? null : slug;
        string? newUrl = string.IsNullOrEmpty(streamUrl) ? null : streamUrl;
        if (_slug == newSlug && _streamUrl == newUrl) return;
        _slug = newSlug;
        _streamUrl = newUrl;
        CurrentArtist = "";
        CurrentTitle = "";
        _nextPoll = DateTime.UtcNow;
    }

    // Back-compat shim — older call sites pass just a slug.
    public void SetChannel(string? slug) => SetSource(slug, null);

    public void Tick()
    {
        if (DateTime.UtcNow < _nextPoll) return;
        if (_inflight != null && !_inflight.IsCompleted) return;

        if (!string.IsNullOrEmpty(_slug))
        {
            _nextPoll = DateTime.UtcNow.AddSeconds(20);
            _inflight = FetchSomaAsync(_slug!);
        }
        else if (!string.IsNullOrEmpty(_streamUrl))
        {
            // ICY polls cost more (one short HTTP read per poll) so back off.
            _nextPoll = DateTime.UtcNow.AddSeconds(30);
            _inflight = FetchIcyAsync(_streamUrl!);
        }
    }

    private async Task FetchSomaAsync(string slug)
    {
        try
        {
            var url = $"https://somafm.com/songs/{slug}.json";
            var resp = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty("songs", out var songs)
                && songs.ValueKind == JsonValueKind.Array
                && songs.GetArrayLength() > 0)
            {
                var first = songs[0];
                CurrentArtist = first.TryGetProperty("artist", out var a)
                    ? a.GetString() ?? "" : "";
                CurrentTitle = first.TryGetProperty("title", out var t)
                    ? t.GetString() ?? "" : "";
            }
        }
        catch { /* keep previous values */ }
    }

    private async Task FetchIcyAsync(string streamUrl)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            req.Headers.Add("Icy-MetaData", "1");
            req.Headers.UserAgent.TryParseAdd("MouseHouseRadio/1.0");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return;

            // The metadata interval — bytes of audio between every metadata
            // block. Lives on either response.Headers or content.Headers
            // depending on the server's framing; check both.
            int metaInt = 0;
            if (resp.Headers.TryGetValues("icy-metaint", out var hv)
                && int.TryParse(hv.FirstOrDefault(), out var v1)) metaInt = v1;
            else if (resp.Content.Headers.TryGetValues("icy-metaint", out var cv)
                && int.TryParse(cv.FirstOrDefault(), out var v2)) metaInt = v2;
            if (metaInt <= 0) return;   // station doesn't expose ICY metadata

            using var stream = await resp.Content.ReadAsStreamAsync();
            // Skip one audio block.
            byte[] skipBuf = new byte[4096];
            int skipped = 0;
            while (skipped < metaInt)
            {
                int toRead = Math.Min(skipBuf.Length, metaInt - skipped);
                int n = await stream.ReadAsync(skipBuf.AsMemory(0, toRead));
                if (n <= 0) return;
                skipped += n;
            }

            // Length byte: number of 16-byte units.
            int lenByte = stream.ReadByte();
            if (lenByte <= 0) return;
            int metaLen = lenByte * 16;
            byte[] metaBuf = new byte[metaLen];
            int read = 0;
            while (read < metaLen)
            {
                int n = await stream.ReadAsync(metaBuf.AsMemory(read, metaLen - read));
                if (n <= 0) break;
                read += n;
            }

            string raw = Encoding.UTF8.GetString(metaBuf, 0, read).TrimEnd('\0');
            string? title = ExtractStreamTitle(raw);
            if (string.IsNullOrWhiteSpace(title)) return;

            // Split on " - " (or em dash) to separate artist from title.
            int dash = title.IndexOf(" - ", StringComparison.Ordinal);
            if (dash < 0) dash = title.IndexOf(" — ", StringComparison.Ordinal);
            if (dash > 0)
            {
                CurrentArtist = title[..dash].Trim();
                CurrentTitle = title[(dash + 3)..].Trim();
            }
            else
            {
                CurrentArtist = "";
                CurrentTitle = title.Trim();
            }
        }
        catch { /* keep previous values */ }
    }

    private static string? ExtractStreamTitle(string raw)
    {
        const string key = "StreamTitle='";
        int s = raw.IndexOf(key, StringComparison.Ordinal);
        if (s < 0) return null;
        s += key.Length;
        int e = raw.IndexOf("';", s, StringComparison.Ordinal);
        if (e < 0) return null;
        return raw[s..e];
    }
}
