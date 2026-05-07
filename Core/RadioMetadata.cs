using System.Text.Json;

namespace MouseHouse.Core;

/// <summary>
/// Polls SomaFM's per-channel now-playing JSON endpoint
/// (https://somafm.com/songs/&lt;slug&gt;.json) every ~20 seconds and exposes
/// the current track artist + title for the radio widget to display. No-op
/// if the channel has no slug or the network is unreachable; failures fall
/// back to the last known values.
///
/// Non-SomaFM streams (e.g. WCPE) don't show now-playing info — pulling ICY
/// metadata directly from the audio stream URL was tried and reverted: it
/// opened a second HTTP connection to the same stream, which on
/// connection-limited Icecast servers like ibiblio stalled ffmpeg's audio
/// connection and blocked playback for several seconds.
/// </summary>
public class RadioMetadata
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public string CurrentArtist { get; private set; } = "";
    public string CurrentTitle { get; private set; } = "";

    private string? _slug;
    private DateTime _nextPoll = DateTime.MinValue;
    private Task? _inflight;

    public bool HasTrack => !string.IsNullOrEmpty(CurrentTitle);

    public void SetChannel(string? slug)
    {
        if (_slug == slug) return;
        _slug = string.IsNullOrEmpty(slug) ? null : slug;
        CurrentArtist = "";
        CurrentTitle = "";
        // Poll on next Tick so a station change shows the new track ASAP.
        _nextPoll = DateTime.UtcNow;
    }

    // Back-compat shim for callers that pass the stream URL too — non-SomaFM
    // stations have no metadata source we can use without stalling audio, so
    // we just ignore the URL.
    public void SetSource(string? slug, string? streamUrl) => SetChannel(slug);

    public void Tick()
    {
        if (string.IsNullOrEmpty(_slug)) return;
        if (DateTime.UtcNow < _nextPoll) return;
        if (_inflight != null && !_inflight.IsCompleted) return;
        _nextPoll = DateTime.UtcNow.AddSeconds(20);
        _inflight = FetchAsync(_slug);
    }

    private async Task FetchAsync(string slug)
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
}
