using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LyrenneVR;

/// <summary>Time-synced lyrics from LRCLIB, the same free source Lyrenne uses.</summary>
public sealed partial class Lyrics
{
    public sealed record Line(TimeSpan At, string Text);

    static readonly HttpClient Http = CreateClient();
    readonly ConcurrentDictionary<string, IReadOnlyList<Line>?> _cache = new();
    readonly ConcurrentDictionary<string, byte> _inFlight = new();

    public event Action? Loaded;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10), BaseAddress = new Uri("https://lrclib.net/") };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LyrenneVR/1.0 (https://github.com/Doctordefector/Lyrenne)");
        return c;
    }

    /// <summary>Current and next line for the track, or nulls while loading or when there are none.</summary>
    public (string? Current, string? Next) At(NowPlaying np)
    {
        if (string.IsNullOrEmpty(np.Artist) || np == NowPlaying.Empty) return (null, null);
        string key = $"{np.Artist}\u0001{np.Title}";
        if (!_cache.TryGetValue(key, out var lines))
        {
            if (_inFlight.TryAdd(key, 0)) _ = FetchAsync(key, np);
            return (null, null);
        }
        if (lines == null || lines.Count == 0) return (null, null);

        // Lyrics feel late without a small lead: the eye reads the line just before it is sung.
        var t = np.Position + TimeSpan.FromMilliseconds(250);
        int i = -1;
        for (int k = 0; k < lines.Count && lines[k].At <= t; k++) i = k;
        string? current = i >= 0 ? lines[i].Text : null;
        string? next = i + 1 < lines.Count ? lines[i + 1].Text : null;
        return (current, next);
    }

    async Task FetchAsync(string key, NowPlaying np)
    {
        IReadOnlyList<Line>? result = null;
        try
        {
            var url = "api/get?artist_name=" + Uri.EscapeDataString(np.Artist) +
                      "&track_name=" + Uri.EscapeDataString(np.Title);
            if (!string.IsNullOrEmpty(np.Album)) url += "&album_name=" + Uri.EscapeDataString(np.Album);
            if (np.Duration > TimeSpan.Zero) url += "&duration=" + (int)Math.Round(np.Duration.TotalSeconds);
            var hit = await TryGet(url);
            if (hit?.SyncedLyrics == null)
            {
                // The exact lookup is strict about album and duration; a search is more forgiving.
                var search = await Http.GetFromJsonAsync<Entry[]>(
                    "api/search?track_name=" + Uri.EscapeDataString(np.Title) + "&artist_name=" + Uri.EscapeDataString(np.Artist));
                hit = search?.Where(e => e.SyncedLyrics != null)
                    .OrderBy(e => np.Duration > TimeSpan.Zero ? Math.Abs(e.Duration - np.Duration.TotalSeconds) : 0)
                    .FirstOrDefault();
            }
            if (hit?.SyncedLyrics != null) result = Parse(hit.SyncedLyrics);
        }
        catch (Exception e)
        {
            Log.Write($"lyrics lookup failed for {np.Title}: {e.Message}");
        }
        _cache[key] = result;
        _inFlight.TryRemove(key, out _);
        Loaded?.Invoke();
    }

    static async Task<Entry?> TryGet(string url)
    {
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<Entry>();
    }

    [GeneratedRegex(@"\[(\d+):(\d+(?:\.\d+)?)\]")]
    private static partial Regex Stamp();

    public static IReadOnlyList<Line> Parse(string lrc)
    {
        var lines = new List<Line>();
        foreach (var raw in lrc.Split('\n'))
        {
            var stamps = Stamp().Matches(raw);
            if (stamps.Count == 0) continue;
            string text = raw[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match m in stamps)
            {
                var at = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) +
                         TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                lines.Add(new Line(at, text));
            }
        }
        lines.Sort((a, b) => a.At.CompareTo(b.At));
        return lines;
    }

    sealed class Entry
    {
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
        [JsonPropertyName("duration")] public double Duration { get; set; }
    }
}
