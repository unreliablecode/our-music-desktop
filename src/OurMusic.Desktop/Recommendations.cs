using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace OurMusic.Desktop;

public static class Recommendations
{
    private static string CachePath => Path.Combine(LibraryStore.Root, "recommendations.json");
    public static List<Track> Load()
    {
        foreach (var path in new[] { CachePath, Path.Combine(AppContext.BaseDirectory, "recommendations.json") })
        {
            try
            {
                var tracks = JsonSerializer.Deserialize<List<Track>>(File.ReadAllText(path));
                if (tracks?.Count > 0) return tracks;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return [];
    }
    public static string ProfileKey(LibraryState state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("|", state.Tracks.OrderBy(t => t.Id).Select(t => $"{t.Id}:{t.Favorite}:{t.PlayCount}")))));
    public static bool NeedsRefresh(LibraryState state)
    {
        try { return !File.Exists(CachePath) || File.GetLastWriteTimeUtc(CachePath) < DateTime.UtcNow.AddHours(-12) || File.ReadAllText(CachePath + ".profile") != ProfileKey(state); }
        catch (IOException) { return true; }
    }
    public static void Save(List<Track> tracks, string profile)
    {
        var temporary = CachePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(tracks)); File.Move(temporary, CachePath, true);
        File.WriteAllText(CachePath + ".profile", profile);
    }
}

public static class RecommendationEngine
{
    public static string ArtistKey(string artist) => artist.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase).Replace("VEVO", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
    public static Dictionary<string, double> Preferences(IEnumerable<Track> history)
    {
        var now = DateTime.UtcNow;
        return history.Where(t => t.Url.Length > 0 && t.Artist.Length > 0 && (t.PlayCount > 0 || t.Favorite))
            .GroupBy(t => ArtistKey(t.Artist))
            .ToDictionary(g => g.Key, g => g.Sum(t => (t.Favorite ? 8 : 0) + Math.Min(t.PlayCount, 30) * 2
                + (t.LastPlayedUtc.HasValue ? Math.Max(0, 4 - (now - t.LastPlayedUtc.Value).TotalDays / 7) : 0)));
    }
    public static List<Track> Rank(IEnumerable<Track> candidates, LibraryState state, int limit = 20)
    {
        var preferences = Preferences(state.Tracks);
        var history = state.Tracks.ToDictionary(t => t.Id);
        var ordered = candidates.Where(t => t.Url.Length > 0 && (t.Duration == 0 || t.Duration is >= 60 and <= 600))
            .DistinctBy(t => t.Id).Select((t, index) =>
            {
                var score = preferences.GetValueOrDefault(ArtistKey(t.Artist)) + 4.0 / (1 + index);
                if (history.TryGetValue(t.Id, out var saved) && saved.LastPlayedUtc > DateTime.UtcNow.AddDays(-1)) score -= 8;
                return (Track: t, Score: score);
            }).OrderByDescending(x => x.Score).Select(x => x.Track).ToList();
        var result = new List<Track>(); var artists = new Dictionary<string, int>();
        foreach (var track in ordered)
        {
            var artist = ArtistKey(track.Artist);
            if (artists.GetValueOrDefault(artist) >= 3) continue;
            result.Add(track); artists[artist] = artists.GetValueOrDefault(artist) + 1;
            if (result.Count == limit) break;
        }
        return result;
    }
    public static async Task<List<Track>> RefreshAsync(YouTubeService youtube, LibraryState state, IEnumerable<Track> seeds, CancellationToken cancellation)
    {
        var artists = Preferences(state.Tracks).OrderByDescending(p => p.Value).Take(2).Select(p => p.Key + " official audio songs");
        var discovery = new[] { "indie pop official audio", "popular songs official audio", "alternative rock official audio", "chill pop official audio" };
        var queries = artists.Append(discovery[DateTime.UtcNow.DayOfYear % discovery.Length]).ToList();
        if (queries.Count == 1) queries.Add("popular songs official music video");
        // Each query preserves YouTube's search ranking. The local ranker adds taste and diversity.
        var results = await Task.WhenAll(queries.Select(async query =>
        {
            try { return await youtube.SearchAsync(query, cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch { return new List<Track>(); }
        }));
        cancellation.ThrowIfCancellationRequested();
        if (results.All(r => r.Count == 0)) throw new IOException("Recommendations could not be refreshed. Showing saved picks.");
        return Rank(results.SelectMany(r => r).Concat(seeds), state);
    }
}
