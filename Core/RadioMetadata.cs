using System.Text.Json;

namespace MouseHouse.Core;

/// <summary>
/// Now-playing fetcher for the radio widget. Two paths:
///   • SomaFM has per-channel JSON at https://somafm.com/songs/&lt;slug&gt;.json
///     — preferred when a slug is set.
///   • WCPE Classical (and any other ibiblio-hosted Icecast stream) has
///     publicly readable Icecast 2 status JSON at
///     /status-json.xsl on the streaming host. We poll that and pull the
///     "title" field of the matching mountpoint. Unlike the previous
///     ICY-from-stream approach this runs on the admin endpoint and never
///     touches the audio connection ffmpeg is using.
/// Failures fall back to the last known values silently.
/// </summary>
public class RadioMetadata
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

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
    /// the stream URL to derive an Icecast status-json endpoint on the same
    /// host (WCPE et al.).
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

    // Back-compat: old call sites that only had a slug.
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
            _nextPoll = DateTime.UtcNow.AddSeconds(30);
            _inflight = FetchIcecastStatusAsync(_streamUrl!);
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
        catch
        {
            // No network / transient failure → keep the last values.
        }
    }

    private async Task FetchIcecastStatusAsync(string streamUrl)
    {
        // Icecast 2's public admin page is at /status-json.xsl on the same
        // host as the stream. It returns a JSON document with one
        // "source" entry per mountpoint (or an array when there's more
        // than one); each entry carries the current StreamTitle as `title`.
        try
        {
            var u = new Uri(streamUrl);
            var statusUrl = $"{u.Scheme}://{u.Authority}/status-json.xsl";
            var resp = await Http.GetStringAsync(statusUrl);
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("icestats", out var stats)) return;
            if (!stats.TryGetProperty("source", out var source)) return;

            JsonElement match = default;
            bool found = false;
            string mountPath = u.AbsolutePath;
            if (source.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in source.EnumerateArray())
                {
                    if (s.TryGetProperty("listenurl", out var lu)
                        && (lu.GetString() ?? "").EndsWith(mountPath, StringComparison.Ordinal))
                    {
                        match = s; found = true; break;
                    }
                }
                if (!found && source.GetArrayLength() > 0)
                {
                    match = source[0]; found = true;
                }
            }
            else if (source.ValueKind == JsonValueKind.Object)
            {
                match = source; found = true;
            }
            if (!found) return;

            string title = match.TryGetProperty("title", out var tEl)
                ? tEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(title)) return;

            // Most Icecast titles arrive as "Artist - Title" (em dash on
            // some stations). Split on the first such separator; otherwise
            // the whole string goes in Title and Artist stays empty.
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
        catch
        {
            // Endpoint missing / network error — keep last values.
        }
    }
}
