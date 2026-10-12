using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OurMusic.Desktop;
public partial class MainWindow : Window
{
    private readonly LibraryStore store = new();
    private readonly YouTubeService youtube = new();
    private readonly MediaPlayer player = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource searchCancellation = new(), playCancellation = new(), lifetime = new();
    private List<Track> visible = [];
    private List<Track> recommended = Recommendations.Load();
    private bool refreshingRecommendations;
    private string page = "Home";
    private Playlist? playlist;
    private Track? current;
    private bool playing, seeking, shuffle, opened, closing;
    private int repeat;
    private DateTime? sleepAt;
    public MainWindow()
    {
        InitializeComponent();
        player.MediaOpened += (_, _) =>
        {
            opened = true; player.Play(); playing = true; PlayButton.Content = "Ⅱ";
            if (current != null) { current.PlayCount++; current.LastPlayedUtc = DateTime.UtcNow; Save(); }
            Status("Playing " + current?.Title);
        };
        player.MediaEnded += async (_, _) => { if (repeat == 2) { player.Position = TimeSpan.Zero; player.Play(); } else await AdvanceAsync(1, true); };
        player.MediaFailed += (_, e) => { opened = playing = false; PlayButton.Content = "▶"; Status("Playback failed: " + e.ErrorException.Message + " Try saving the song offline."); };
        timer.Tick += (_, _) => Tick(); timer.Start();
        VolumeSlider.Value = store.State.Volume; player.Volume = store.State.Volume;
        ApplyTheme(); RefreshPlaylists(); RefreshQueue(); RefreshPage();
        if (store.LoadWarning != null) Status(store.LoadWarning);
    }
    private void Status(string value) { if (!closing) StatusText.Text = value; }
    private void Save() { try { store.Save(); } catch (Exception ex) { Status("Could not save library: " + ex.Message); } }
    private void ShowTracks(IEnumerable<Track> tracks)
    {
        visible = tracks.ToList(); TrackList.ItemsSource = visible;
        EmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshPage(bool animate = true)
    {
        PageTitle.Text = playlist?.Name ?? page;
        IEnumerable<Track> tracks = store.State.Tracks;
        if (playlist != null) tracks = playlist.TrackIds.Select(id => store.State.Tracks.FirstOrDefault(t => t.Id == id)).OfType<Track>();
        else tracks = page switch { "Favorites" => tracks.Where(t => t.Favorite), "Offline" => tracks.Where(t => File.Exists(t.LocalPath)), "Home" => recommended.Count > 0 ? recommended : tracks.TakeLast(20).Reverse(), _ => tracks };
        if (page != "Search" || playlist != null) ShowTracks(tracks);
        PageSubtitle.Text = page == "Home" ? RecommendationEngine.Preferences(store.State.Tracks).Count > 0
            ? "Recommended from your listening history, with a few new discoveries."
            : "A mix to get you started. Your picks adapt as you listen and favorite songs."
            : $"{visible.Count} songs · Double-click to play";
        HomeBanner.Visibility = page == "Home" && visible.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var featured = page == "Home" ? visible.FirstOrDefault() : null;
        HeroArtwork.Track = featured; HeroTitle.Text = featured?.Title ?? ""; HeroArtist.Text = featured?.Artist ?? "";
        foreach (var button in NavigationPanel.Children.OfType<Button>())
            button.SetResourceReference(Button.BackgroundProperty, (string)button.Tag == page ? "AccentBrush" : "SurfaceBrush");
        if (animate) AnimatePage();
    }
    private void AnimatePage()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PagePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
        ((TranslateTransform)PagePanel.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }
    private void Window_SourceInitialized(object? sender, EventArgs e) => WindowTheme.Apply(this, store.State.DarkMode);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadRecommendationsAsync();
    private async void RefreshPicks_Click(object sender, RoutedEventArgs e) => await LoadRecommendationsAsync(true);
    private async void PlayFeatured_Click(object sender, RoutedEventArgs e)
    {
        if (page != "Home" || visible.Count == 0) return;
        store.State.Queue = visible.ToList(); await PlayAtAsync(0);
    }
    private async Task LoadRecommendationsAsync(bool force = false)
    {
        if (refreshingRecommendations || (!force && !Recommendations.NeedsRefresh(store.State))) return;
        refreshingRecommendations = true; RefreshPicksButton.IsEnabled = false;
        var profile = Recommendations.ProfileKey(store.State);
        if (page == "Home") Status("Updating your recommendations…");
        try
        {
            var updated = await RecommendationEngine.RefreshAsync(youtube, store.State, recommended, lifetime.Token);
            if (updated.Count == 0) return;
            recommended = updated; Recommendations.Save(updated, profile);
            if (page == "Home" && !closing) { RefreshPage(false); Status("Recommendations updated."); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (page == "Home") Status(ex.Message); }
        finally { refreshingRecommendations = false; RefreshPicksButton.IsEnabled = true; }
    }
    private void RefreshPlaylists() { PlaylistList.ItemsSource = null; PlaylistList.ItemsSource = store.State.Playlists; }
    private void RefreshQueue() { QueueList.ItemsSource = null; QueueList.ItemsSource = store.State.Queue; QueueList.SelectedIndex = store.State.QueueIndex; }
    private Track? Selected() { if (TrackList.SelectedItem is Track track) return store.State.Tracks.FirstOrDefault(t => t.Id == track.Id) ?? track; Status("Select a song first."); return null; }
    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        searchCancellation.Cancel(); playlist = null; PlaylistList.SelectedItem = null;
        page = (string)((Button)sender).Tag; RefreshPage();
        if (page == "Home") await LoadRecommendationsAsync();
    }
    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await SearchAsync(); }
    private async Task SearchAsync()
    {
        var query = SearchBox.Text.Trim(); if (query.Length == 0) { Status("Enter a song, artist, or YouTube link."); return; }
        searchCancellation.Cancel(); searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = searchCancellation.Token; SearchButton.Content = "Searching…"; Status("Searching YouTube…");
        try { var results = await youtube.SearchAsync(query, token); token.ThrowIfCancellationRequested(); playlist = null; PlaylistList.SelectedItem = null; page = "Search"; ShowTracks(results); RefreshPage(); PageTitle.Text = "Search results"; Status($"{results.Count} results · Select a song to play, save, or add to a playlist."); }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) Status("Search timed out. Try again."); }
        catch (Exception ex) { Status("Search failed: " + ex.Message); }
        finally { if (!closing && token == searchCancellation.Token) SearchButton.Content = "Search"; }
    }
    private async Task PlayAtAsync(int index)
    {
        if (index < 0 || index >= store.State.Queue.Count) return;
        playCancellation.Cancel(); playCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var token = playCancellation.Token;
        player.Stop(); opened = playing = false; PlayButton.Content = "▶"; SeekBar.Value = 0;
        store.State.QueueIndex = index; current = store.Add(store.State.Queue[index]);
        store.State.Queue[index] = current;
        NowTitle.Text = current.Title; NowArtist.Text = current.Artist; NowArtwork.Track = current; RefreshQueue(); Save(); Status("Opening " + current.Title + "…");
        try
        {
            var source = File.Exists(current.LocalPath) ? current.LocalPath : current.Url.Length > 0 ? await youtube.ResolveAsync(current, token) : throw new FileNotFoundException("The imported audio file has moved or been deleted.");
            token.ThrowIfCancellationRequested();
            player.Open(new Uri(source.Split('\n')[0].Trim(), UriKind.Absolute));
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) Status("Playback request timed out."); }
        catch (Exception ex) { Status("Unable to play: " + ex.Message); }
    }
    private async Task AdvanceAsync(int direction, bool ended = false)
    {
        var count = store.State.Queue.Count; if (count == 0) return;
        var next = store.State.QueueIndex + direction;
        if (shuffle && count > 1) { do { next = Random.Shared.Next(count); } while (next == store.State.QueueIndex); }
        if (next >= count) { if (repeat == 1) next = 0; else { if (ended) { playing = false; PlayButton.Content = "▶"; } return; } }
        if (next < 0) next = repeat == 1 ? count - 1 : 0;
        await PlayAtAsync(next);
    }
    private async void TrackList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackList.SelectedItem is not Track track) return;
        store.State.Queue = visible.ToList(); await PlayAtAsync(visible.IndexOf(track));
    }
    private async void PlayAll_Click(object sender, RoutedEventArgs e) { if (visible.Count == 0) return; store.State.Queue = visible.ToList(); await PlayAtAsync(0); }
    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!opened) { if (store.State.Queue.Count > 0) await PlayAtAsync(Math.Max(0, store.State.QueueIndex)); return; }
        if (playing) player.Pause(); else player.Play(); playing = !playing; PlayButton.Content = playing ? "Ⅱ" : "▶";
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) { if (opened && player.Position.TotalSeconds > 3) player.Position = TimeSpan.Zero; else await AdvanceAsync(-1); }
    private async void Next_Click(object sender, RoutedEventArgs e) => await AdvanceAsync(1);
    private void Enqueue_Click(object sender, RoutedEventArgs e) { if (Selected() is not Track track) return; store.State.Queue.Add(store.Add(track)); RefreshQueue(); Save(); Status("Added to queue."); }
    private async void QueueList_DoubleClick(object sender, MouseButtonEventArgs e) { if (QueueList.SelectedIndex >= 0) await PlayAtAsync(QueueList.SelectedIndex); }
    private void ClearQueue_Click(object sender, RoutedEventArgs e) { playCancellation.Cancel(); player.Stop(); opened = playing = false; current = null; NowArtwork.Track = null; PlayButton.Content = "▶"; NowTitle.Text = "Nothing playing yet"; NowArtist.Text = "Double-click a song to play"; SeekBar.Value = 0; TimeText.Text = "0:00 / 0:00"; store.State.Queue.Clear(); store.State.QueueIndex = -1; RefreshQueue(); Save(); Status("Queue cleared."); }
    private void RemoveQueue_Click(object sender, RoutedEventArgs e)
    {
        int index = QueueList.SelectedIndex; if (index < 0) return;
        if (index == store.State.QueueIndex) { playCancellation.Cancel(); player.Stop(); opened = playing = false; current = null; PlayButton.Content = "▶"; }
        store.State.Queue.RemoveAt(index); if (index <= store.State.QueueIndex) store.State.QueueIndex--; RefreshQueue(); Save();
    }
    private void Favorite_Click(object sender, RoutedEventArgs e) { if (Selected() is not Track track) return; var saved = store.Add(track); saved.Favorite = !saved.Favorite; Save(); Status(saved.Favorite ? "Added to favorites." : "Removed from favorites."); RefreshPage(); }
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not Track track) return;
        if (File.Exists(track.LocalPath)) { Status("This song is already available offline."); return; }
        if (track.Url.Length == 0) { Status("Original audio file is missing. Import it again."); return; }
        var button = (Button)sender; button.IsEnabled = false; Status("Saving audio for offline playback…");
        try { var path = await youtube.DownloadAsync(track, lifetime.Token); if (!File.Exists(path)) throw new IOException("Downloaded audio was not found."); store.Add(track).LocalPath = path; Save(); RefreshPage(); Status("Saved offline: " + track.Title); }
        catch (OperationCanceledException) { Status("Download cancelled or timed out."); }
        catch (Exception ex) { Status("Download failed: " + ex.Message); }
        finally { button.IsEnabled = true; }
    }
    private string? Prompt(string title, string initial = "", bool multiline = false)
    {
        var dialog = new Window { Owner = this, Title = title, Width = 460, Height = multiline ? 470 : 190, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("CanvasBrush"), ResizeMode = ResizeMode.NoResize };
        dialog.SourceInitialized += (_, _) => WindowTheme.Apply(dialog, store.State.DarkMode);
        var panel = new DockPanel { Margin = new Thickness(20) }; var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var save = new Button { Content = "Save", IsDefault = !multiline };
        buttons.Children.Add(cancel); buttons.Children.Add(save); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var input = new TextBox { Text = initial, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Margin = new Thickness(0, 0, 0, 15) };
        panel.Children.Add(input); dialog.Content = panel; save.Click += (_, _) => dialog.DialogResult = true; dialog.Loaded += (_, _) => input.Focus();
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
    private void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("Create playlist"); if (string.IsNullOrWhiteSpace(name)) return;
        if (store.State.Playlists.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { Status("A playlist with that name already exists."); return; }
        var created = new Playlist { Name = name }; store.State.Playlists.Add(created); RefreshPlaylists(); PlaylistList.SelectedItem = created; Save();
    }
    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (PlaylistList.SelectedItem is Playlist selected) { searchCancellation.Cancel(); playlist = selected; page = "Playlist"; RefreshPage(); } }
    private void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not Track track) return;
        if (store.State.Playlists.Count == 0) { Status("Create a playlist first."); return; }
        var dialog = new Window { Owner = this, Title = "Choose playlist", Width = 360, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("CanvasBrush") };
        dialog.SourceInitialized += (_, _) => WindowTheme.Apply(dialog, store.State.DarkMode);
        var panel = new DockPanel { Margin = new Thickness(16) }; var add = new Button { Content = "Add song", IsDefault = true }; DockPanel.SetDock(add, Dock.Bottom); panel.Children.Add(add);
        var list = new ListBox { ItemsSource = store.State.Playlists, DisplayMemberPath = "Name", SelectedIndex = 0 }; panel.Children.Add(list); dialog.Content = panel;
        add.Click += (_, _) => { if (list.SelectedItem is Playlist chosen) { var saved = store.Add(track); if (!chosen.TrackIds.Contains(saved.Id)) chosen.TrackIds.Add(saved.Id); Save(); dialog.DialogResult = true; } };
        if (dialog.ShowDialog() == true) { Status("Song added to playlist."); RefreshPage(); }
    }
    private void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not Track track) return;
        if (playlist != null) playlist.TrackIds.Remove(track.Id);
        else { store.State.Tracks.RemoveAll(t => t.Id == track.Id); foreach (var p in store.State.Playlists) p.TrackIds.Remove(track.Id); }
        Save(); if (page == "Search") ShowTracks(visible.Where(t => t.Id != track.Id)); else RefreshPage(); Status("Removed. Audio files remain on disk.");
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Multiselect = true, Filter = "Audio files|*.mp3;*.m4a;*.wav;*.wma;*.aac;*.mp4|All files|*.*" };
        if (picker.ShowDialog() == true) ImportFiles(picker.FileNames);
    }
    private void ImportFiles(IEnumerable<string> files)
    {
        var count = 0;
        foreach (var file in files.Where(File.Exists))
        {
            if (!new[] { ".mp3", ".m4a", ".wav", ".wma", ".aac", ".mp4" }.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
            var full = Path.GetFullPath(file);
            if (store.State.Tracks.Any(t => t.LocalPath.Equals(full, StringComparison.OrdinalIgnoreCase))) continue;
            store.Add(new Track { Title = Path.GetFileNameWithoutExtension(file), Artist = "Local audio", LocalPath = full }); count++;
        }
        playlist = null; page = "Songs"; PlaylistList.SelectedItem = null; RefreshPage(); Status($"Imported {count} audio files.");
    }
    private void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) ImportFiles(files); }
    private void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        var track = TrackList.SelectedItem as Track ?? current; if (track == null) { Status("Select or play a song first."); return; }
        track = store.State.Tracks.FirstOrDefault(t => t.Id == track.Id) ?? track;
        var lyrics = Prompt("Lyrics / notes · " + track.Title, track.Lyrics, true);
        if (lyrics != null) { store.Add(track).Lyrics = lyrics; Save(); Status("Lyrics / notes saved."); }
    }
    private void Shuffle_Click(object sender, RoutedEventArgs e) { shuffle = !shuffle; ShuffleButton.Content = "Shuffle: " + (shuffle ? "on" : "off"); }
    private void Repeat_Click(object sender, RoutedEventArgs e) { repeat = (repeat + 1) % 3; RepeatButton.Content = "Repeat: " + new[] { "off", "all", "one" }[repeat]; }
    private void SleepTimer_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (SleepTimer?.SelectedItem is ComboBoxItem item) sleepAt = int.TryParse(item.Tag?.ToString(), out var minutes) ? DateTime.Now.AddMinutes(minutes) : null; }
    private void Tick()
    {
        if (sleepAt.HasValue && DateTime.Now >= sleepAt) { player.Pause(); playing = false; PlayButton.Content = "▶"; sleepAt = null; SleepTimer.SelectedIndex = 0; Status("Sleep timer paused playback."); }
        if (!opened) return;
        var total = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : current?.Duration ?? 0;
        SeekBar.Maximum = Math.Max(1, total); if (!seeking) SeekBar.Value = player.Position.TotalSeconds;
        TimeText.Text = player.Position.ToString(@"m\:ss") + " / " + TimeSpan.FromSeconds(total).ToString(@"m\:ss");
    }
    private void Seek_MouseDown(object sender, MouseButtonEventArgs e) => seeking = true;
    private void Seek_MouseUp(object sender, MouseButtonEventArgs e) { if (opened) player.Position = TimeSpan.FromSeconds(SeekBar.Value); seeking = false; }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { player.Volume = e.NewValue; store.State.Volume = e.NewValue; }
    private void Theme_Click(object sender, RoutedEventArgs e) { store.State.DarkMode = !store.State.DarkMode; ApplyTheme(); Save(); }
    private void ApplyTheme()
    {
        var colors = store.State.DarkMode ? new[] { "#121C24", "#1D2C38", "#233543", "#EDF5FC", "#B0C5D6", "#35566F" } : new[] { "#F2F8FC", "#E4EEF6", "#FFFFFF", "#182832", "#556976", "#D0E4F7" };
        var keys = new[] { "CanvasBrush", "SurfaceBrush", "CardBrush", "TextBrush", "MutedBrush", "AccentBrush" };
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        WindowTheme.Apply(this, store.State.DarkMode);
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { closing = true; lifetime.Cancel(); searchCancellation.Cancel(); playCancellation.Cancel(); timer.Stop(); player.Close(); Save(); }
}
