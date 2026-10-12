using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using OurMusic.Desktop;

internal static class Program
{
    private static int failures;
    [STAThread]
    public static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "MusicYouTests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MUSIC_YOU_DATA_DIR", root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OurMusic.Desktop;component/Theme.xaml", UriKind.Relative) });
        app.Startup += async (_, _) =>
        {
            try { await Run(args); }
            catch (Exception ex) { failures++; Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        };
        app.Run(); Console.WriteLine(failures == 0 ? "All smoke checks passed." : "Smoke checks failed."); return failures == 0 ? 0 : 1;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static Button Button() => new();
    private static async Task Run(string[] args)
    {
        var store = new LibraryStore();
        var wav = Path.Combine(LibraryStore.Root, "test.wav");
        using (var writer = new BinaryWriter(File.Create(wav)))
        {
            const int samples = 44100 * 5;
            writer.Write("RIFF"u8.ToArray()); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8.ToArray()); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8.ToArray()); writer.Write(samples * 2);
            for (int i = 0; i < samples; i++) writer.Write((short)0);
        }
        var first = store.Add(new Track { Id = "local-one", Title = "Local playback check", LocalPath = wav, Artist = "Smoke tests", Duration = 5 });
        var second = store.Add(new Track { Id = "local-two", Title = "Queue check", LocalPath = wav, Duration = 5 });
        Check(store.Add(first).Id == first.Id && store.State.Tracks.Count == 2, "Library deduplicates songs");
        store.State.Playlists.Add(new Playlist { Name = "Evening mix", TrackIds = [first.Id] }); store.Save();
        Check(new LibraryStore().State.Playlists[0].TrackIds[0] == first.Id, "Playlists persist");
        var window = new MainWindow();
        Check(window.Title == "Our Music - Desktop", "Window uses Our Music branding");
        Check(((ListBox)window.FindName("TrackList")).Items.Count > 0 && ((Border)window.FindName("HomeBanner")).Visibility == Visibility.Visible, "Home shows recommendations on first load");
        var navigation = Button(); navigation.Tag = "Songs"; Invoke(window, "Navigate_Click", navigation, new RoutedEventArgs());
        var tracks = (ListBox)window.FindName("TrackList"); tracks.SelectedItem = tracks.Items.Cast<Track>().First(t => t.Id == first.Id);
        Invoke(window, "Favorite_Click", Button(), new RoutedEventArgs());
        Check(new LibraryStore().State.Tracks.First(t => t.Id == first.Id).Favorite, "Favorites persist");
        Invoke(window, "Enqueue_Click", Button(), new RoutedEventArgs());
        Check(Field<LibraryStore>(window, "store").State.Queue.Count == 1, "Queue accepts selected song");
        Invoke(window, "Theme_Click", Button(), new RoutedEventArgs());
        Check(new LibraryStore().State.DarkMode, "Theme persists");
        var handle = new WindowInteropHelper(window).EnsureHandle(); WindowTheme.Apply(window, true);
        Check(window.WindowStyle == WindowStyle.None && ((SolidColorBrush)((Border)window.FindName("TitleBar")).Background).Color == ((SolidColorBrush)window.Background).Color, "Title bar matches the app theme");
        Check(((FrameworkElement)window.FindName("PagePanel")).HasAnimatedProperties, "Page changes have transition animations");
        ((Slider)window.FindName("VolumeSlider")).Value = 0;
        if (!args.Contains("--skip-audio"))
        {
            await (Task)Invoke(window, "PlayAtAsync", 0)!;
            await Task.Delay(1800);
            Check(Field<bool>(window, "opened"), "Native audio opens local WAV");
            Check(Field<MediaPlayer>(window, "player").Position.TotalSeconds > 0, "Audio playback advances");
            Check(new LibraryStore().State.Tracks.First(t => t.Id == first.Id).PlayCount == 1, "Successful playback records listening history");
            Invoke(window, "PlayPause_Click", Button(), new RoutedEventArgs());
            Check(!Field<bool>(window, "playing"), "Pause stops playback");
        }
        else Console.WriteLine("SKIP Audio playback checks (headless runner)");
        navigation.Tag = "Offline"; Invoke(window, "Navigate_Click", navigation, new RoutedEventArgs());
        Check(tracks.Items.Count == 2, "Offline page shows local songs");
        Invoke(window, "ClearQueue_Click", Button(), new RoutedEventArgs());
        Check(Field<LibraryStore>(window, "store").State.QueueIndex == -1 && !Field<bool>(window, "playing"), "Clear queue stops audio");
        Check(ScrollViewer.GetHorizontalScrollBarVisibility(tracks) == ScrollBarVisibility.Disabled && ScrollViewer.GetVerticalScrollBarVisibility(tracks) == ScrollBarVisibility.Hidden, "Track list hides scrollbars while keeping scrolling");
        var taste = new LibraryState { Tracks = [new Track { Id = "heard", Artist = "Favorite Artist", Url = "https://www.youtube.com/watch?v=heard", Favorite = true, PlayCount = 5 }] };
        var candidates = Enumerable.Range(0, 8).Select(i => new Track { Id = "candidate" + i, Artist = i < 5 ? "Favorite Artist" : "Discovery " + i, Url = "https://www.youtube.com/watch?v=candidate" + i, Duration = 180 }).ToList();
        var ranked = RecommendationEngine.Rank(candidates, taste);
        Check(ranked[0].Artist == "Favorite Artist" && ranked.Count(t => t.Artist == "Favorite Artist") == 3 && ranked.Any(t => t.Artist.StartsWith("Discovery")), "Recommendation ranking uses taste and limits repeated artists");
        candidates.Add(candidates[0]); candidates.Add(new Track { Id = "compilation", Url = "https://www.youtube.com/watch?v=compilation", Duration = 3600 });
        ranked = RecommendationEngine.Rank(candidates, taste);
        Check(ranked.DistinctBy(t => t.Id).Count() == ranked.Count && ranked.All(t => t.Id != "compilation"), "Recommendations exclude duplicates and long compilations");
        if (args.Contains("--online"))
        {
            var youtube = new YouTubeService(); var results = await youtube.SearchAsync("Queen Radio Ga Ga", CancellationToken.None);
            Check(results.Count > 0 && results[0].Url.StartsWith("https://www.youtube.com/"), "Live YouTube search returns songs");
            var cover = await ArtworkCache.LoadAsync(results[0].Thumbnail);
            Check(cover != null && cover.PixelWidth > 0, "Cover images download and decode" + (ArtworkCache.LastError == null ? "" : ": " + ArtworkCache.LastError));
            await (Task)Invoke(window, "LoadRecommendationsAsync", true)!;
            Check(Field<List<Track>>(window, "recommended").Count > 0 && File.Exists(Path.Combine(LibraryStore.Root, "recommendations.json")), "Live recommendations refresh and persist");
            var online = results[0]; Field<LibraryStore>(window, "store").State.Queue = [online];
            await (Task)Invoke(window, "PlayAtAsync", 0)!; await Task.Delay(8000);
            Check(Field<bool>(window, "opened"), "Native player opens YouTube audio stream");
            Check(Field<MediaPlayer>(window, "player").Position.TotalSeconds > 0, "YouTube audio playback advances");
            var download = await youtube.DownloadAsync(online, CancellationToken.None);
            Check(File.Exists(download) && new FileInfo(download).Length > 0, "Offline audio download creates a playable file");
            online.LocalPath = download; Field<LibraryStore>(window, "store").Add(online).LocalPath = download;
            Invoke(window, "ClearQueue_Click", Button(), new RoutedEventArgs());
        }
        navigation.Tag = "Home";
        // Render the page without starting a second background refresh in offline tests.
        typeof(MainWindow).GetField("page", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, "Home");
        Invoke(window, "RefreshPage", true); await Task.Delay(600);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1280, 900)); content.Arrange(new Rect(0, 0, 1280, 900)); content.UpdateLayout();
        await Task.Delay(2000);
        var scroll = FindVisual<ScrollViewer>(tracks);
        if (scroll != null && scroll.ScrollableHeight > 0)
        {
            scroll.ScrollToVerticalOffset(2); content.UpdateLayout();
            Check(scroll.VerticalOffset > 0, "Hidden scrollbars preserve vertical scrolling");
            scroll.ScrollToHome(); content.UpdateLayout();
        }
        Check(((ComboBox)window.FindName("SleepTimer")).Template != null, "Sleep timer has a themed control template");
        var bannerCover = (CoverArt)window.FindName("HeroArtwork");
        if (args.Contains("--online")) Check(bannerCover.HasArtwork, "Home banner displays downloaded cover artwork");
        var bitmap = new RenderTargetBitmap(1280, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var output = args.FirstOrDefault(a => a.StartsWith("--output="))?[9..] ?? LibraryStore.Root;
        Directory.CreateDirectory(output); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "desktop-preview.png"))) encoder.Save(file);
        Invoke(window, "Window_Closing", null, new System.ComponentModel.CancelEventArgs());
        var persisted = new LibraryStore(); Check(persisted.State.Volume == 0 && persisted.State.Queue.Count == 0, "Volume and queue survive restart");
        window.Close(); Console.WriteLine("Preview: " + Path.Combine(output, "desktop-preview.png"));
    }
    private static T? FindVisual<T>(DependencyObject element) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T result) return result;
            var descendant = FindVisual<T>(child); if (descendant != null) return descendant;
        }
        return null;
    }
}
