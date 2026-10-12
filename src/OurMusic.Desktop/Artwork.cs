using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OurMusic.Desktop;

// Decode downloaded bytes instead of relying on WPF's remote URI image loader.
// Frozen bitmaps are shared, while successful downloads also survive restarts.
public static class ArtworkCache
{
    public static string? LastError { get; private set; }
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, Task<BitmapSource?>> Pending = new();
    public static Task<BitmapSource?> LoadAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return Task.FromResult<BitmapSource?>(null);
        return Pending.GetOrAdd(url, DownloadAsync);
    }
    private static async Task<BitmapSource?> DownloadAsync(string url)
    {
        string? path = null;
        try
        {
            var folder = Path.Combine(LibraryStore.Root, "Artwork");
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".img");
            var bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path) : await Client.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 640; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
            return bitmap;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (path != null && File.Exists(path)) { try { File.Delete(path); } catch { } }
            // Do not cache failed downloads forever: revisiting the page can retry.
            _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => Pending.TryRemove(url, out var ignored));
            return null;
        }
    }
}

public class CoverArt : Grid
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Track), typeof(CoverArt), new PropertyMetadata(null, Changed));
    public Track? Track { get => (Track?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    private readonly Image image = new() { Stretch = Stretch.UniformToFill };
    private readonly Border shape;
    private int version;
    public CoverArt()
    {
        ClipToBounds = true;
        shape = new Border { CornerRadius = new CornerRadius(16) };
        shape.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        var inner = new Grid();
        var note = new TextBlock { Text = "♫", FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        inner.Children.Add(note); inner.Children.Add(image); shape.Child = inner; Children.Add(shape);
        SizeChanged += (_, _) => image.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 16, 16);
        Loaded += async (_, _) => await RefreshAsync();
    }
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e) => ((CoverArt)target).Refresh();
    private async void Refresh() => await RefreshAsync();
    private async Task RefreshAsync()
    {
        var revision = ++version; var track = Track; image.Source = null;
        if (track == null) return;
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(track.Thumbnail)) urls.Add(track.Thumbnail);
        if (track.Url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) && track.Id.Length == 11)
            urls.Add("https://i.ytimg.com/vi/" + Uri.EscapeDataString(track.Id) + "/hqdefault.jpg");
        foreach (var url in urls.Distinct())
        {
            var source = await ArtworkCache.LoadAsync(url);
            if (version != revision) return;
            if (source != null) { image.Source = source; return; }
        }
    }
    public bool HasArtwork => image.Source != null;
}
