using System.IO;
using System.Text.Json;
using System.Diagnostics;

namespace OurMusic.Desktop;

public record Track
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = "Untitled";
    public string Artist { get; init; } = "Unknown artist";
    public string Url { get; init; } = "";
    public string Thumbnail { get; init; } = "";
    public string LocalPath { get; set; } = "";
    public double Duration { get; init; }
    public bool Favorite { get; set; }
    public string Lyrics { get; set; } = "";
    public int PlayCount { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public string DurationLabel => Duration > 0 ? TimeSpan.FromSeconds(Duration).ToString(Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "—";
}
public class Playlist
{
    public string Name { get; set; } = "New playlist";
    public List<string> TrackIds { get; set; } = [];
}
public class LibraryState
{
    public List<Track> Tracks { get; set; } = [];
    public List<Playlist> Playlists { get; set; } = [];
    public List<Track> Queue { get; set; } = [];
    public int QueueIndex { get; set; } = -1;
    public double Volume { get; set; } = 0.7;
    public bool DarkMode { get; set; }
}
public class LibraryStore
{
    public static string Root => Environment.GetEnvironmentVariable("MUSIC_YOU_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicYouDesktop");
    public static string Cache => Path.Combine(Root, "Audio");
    public LibraryState State { get; }
    public string? LoadWarning { get; }
    public LibraryStore()
    {
        Directory.CreateDirectory(Cache);
        try { State = JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(Path.Combine(Root, "library.json"))) ?? new(); }
        catch (FileNotFoundException) { State = new(); }
        catch (Exception ex)
        {
            State = new(); LoadWarning = "Library could not be read: " + ex.Message;
            var path = Path.Combine(Root, "library.json");
            if (File.Exists(path)) File.Copy(path, path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
        }
    }
    public void Save()
    {
        var path = Path.Combine(Root, "library.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    public Track Add(Track track)
    {
        var existing = State.Tracks.FirstOrDefault(t => t.Id == track.Id);
        if (existing != null) return existing;
        State.Tracks.Add(track); Save(); return track;
    }
}
public class YouTubeService
{
    public static string Executable => Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe");
    public async Task<string> RunAsync(IEnumerable<string> args, CancellationToken cancellation = default)
    {
        if (!File.Exists(Executable)) throw new InvalidOperationException("yt-dlp is missing. Run desktop/Setup.ps1, then rebuild the app.");
        var info = new ProcessStartInfo(Executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var arg in new[] { "--ignore-config", "--no-warnings", "--socket-timeout", "20", "--js-runtimes", "node" }.Concat(args)) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start yt-dlp.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask; var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Length > 1600 ? error[^1600..] : error.Trim());
        return output.Trim();
    }
    public async Task<List<Track>> SearchAsync(string query, CancellationToken cancellation)
    {
        var isLink = Uri.TryCreate(query, UriKind.Absolute, out var link) && (link.Host == "youtu.be" || link.Host == "youtube.com" || link.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase));
        var json = await RunAsync(["--flat-playlist", "--playlist-end", "50", "--dump-single-json", "--", isLink ? query : "ytsearch20:" + query], cancellation);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var entries = root.TryGetProperty("entries", out var list) ? list.EnumerateArray().ToArray() : [root];
        return entries.Where(e => e.ValueKind == JsonValueKind.Object).Select(ParseTrack).ToList();
    }
    public static Track ParseTrack(JsonElement e)
    {
        string Value(string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var id = Value("id");
        var thumbnail = Value("thumbnail");
        if (thumbnail.Length == 0 && e.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
            thumbnail = thumbs.EnumerateArray().Where(t => t.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                .OrderByDescending(t => t.TryGetProperty("width", out var w) && w.TryGetInt32(out var width) ? width : 0)
                .Select(t => t.GetProperty("url").GetString() ?? "").FirstOrDefault() ?? "";
        // YouTube also returns WebP thumbnails, which WPF cannot decode on every PC.
        // The stable JPEG endpoint works with Windows' built-in imaging codecs.
        if (id.Length == 11) thumbnail = "https://i.ytimg.com/vi/" + Uri.EscapeDataString(id) + "/hqdefault.jpg";
        var artist = Value("artist"); if (artist.Length == 0) artist = Value("uploader"); if (artist.Length == 0) artist = Value("channel");
        return new Track { Id = id, Title = Value("title"), Artist = artist, Thumbnail = thumbnail, Url = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(id), Duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds) ? seconds : 0 };
    }
    public Task<string> ResolveAsync(Track track, CancellationToken cancellation) => RunAsync(["--no-playlist", "-f", "bestaudio[ext=m4a]/best[ext=mp4]", "--get-url", "--", track.Url], cancellation);
    public async Task<string> DownloadAsync(Track track, CancellationToken cancellation)
    {
        var safeId = string.Concat(track.Id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        var template = Path.Combine(LibraryStore.Cache, safeId + ".%(ext)s");
        return await RunAsync(["--no-playlist", "-f", "bestaudio[ext=m4a]/best[ext=mp4]", "--no-progress", "--print", "after_move:filepath", "-o", template, "--", track.Url], cancellation);
    }
}
