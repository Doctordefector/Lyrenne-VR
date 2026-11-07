using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace LyrenneVR;

/// <summary>
/// Full-size album covers from the iTunes Search API (free, no key). Used when the media session
/// only carries a small thumbnail, as Lyrenne's does.
/// </summary>
public static class Artwork
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<SKImage?> FindAsync(string artist, string title)
    {
        try
        {
            string term = Uri.EscapeDataString($"{artist} {Clean(title)}");
            var result = await Http.GetFromJsonAsync<Response>($"https://itunes.apple.com/search?term={term}&entity=song&limit=8");
            // Only trust a hit by the same artist; a wrong cover is worse than a soft one.
            var hit = result?.Results?.FirstOrDefault(r => r.ArtworkUrl100 != null && SameArtist(r.ArtistName, artist));
            if (hit?.ArtworkUrl100 == null) return null;
            var bytes = await Http.GetByteArrayAsync(hit.ArtworkUrl100.Replace("100x100bb", "600x600bb"));
            return SKImage.FromEncodedData(bytes);
        }
        catch (Exception e)
        {
            Log.Write($"artwork lookup failed for {artist} - {title}: {e.Message}");
            return null;
        }
    }

    // "Song (Official Video) [Remastered 2011]" searches better as "Song".
    static string Clean(string title)
    {
        int cut = title.IndexOfAny(new[] { '(', '[' });
        return (cut > 2 ? title[..cut] : title).Trim();
    }

    static bool SameArtist(string? found, string wanted)
    {
        if (string.IsNullOrEmpty(found)) return false;
        string a = Norm(found), b = Norm(wanted);
        return a.Contains(b) || b.Contains(a);
    }

    static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    sealed class Response
    {
        [JsonPropertyName("results")] public List<Item>? Results { get; set; }
    }

    sealed class Item
    {
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("artworkUrl100")] public string? ArtworkUrl100 { get; set; }
    }
}
