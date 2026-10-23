using BrimstoneXbox.Models;
using BrimstoneXbox.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BrimstoneXbox
{
    public sealed partial class MainPage : Page
    {
        CoreClient _core = new CoreClient();
        readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
        readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        readonly DispatcherTimer _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        readonly DispatcherTimer _toast = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        EndpointServer _server;
        List<CoreAlbum> _albums = new List<CoreAlbum>();
        List<MediaCard> _videos = new List<MediaCard>();
        List<MediaCard> _sectionCards = new List<MediaCard>();
        CoreAlbum _album;
        string _section = "Home", _endpoint = "", _videoId = "";
        long _context, _operation, _seekOperation, _volumeOperation;
        bool _pollBusy, _updatingOutput, _updatingPlayback, _seekEditing, _videoOpening, _initialised;

        public MainPage()
        {
            InitializeComponent();
            Loaded += LoadedAsync;
            _poll.Tick += async (s, e) => await PollAsync();
            _heartbeat.Tick += async (s, e) => await HeartbeatAsync();
            _toast.Tick += (s, e) => { _toast.Stop(); ToastBorder.Visibility = Visibility.Collapsed; };
        }

        async void LoadedAsync(object sender, RoutedEventArgs e)
        {
            if (_initialised) return;
            _initialised = true;
            // R1 retires the Xbox-hosted Core. Never start a local engine, scan,
            // create an administrator, rip a disc or listen for Core API traffic.
            // Package identity and LocalFolder remain unchanged for in-place updates.
            _settings.Values["xboxEdition"] = "control";
            CoreUrlBox.Text = _core.BaseUrl ?? "";
            await PlaybackService.Instance.InitialisePreferredOutputAsync();
            VideoPlayer.SetMediaPlayer(PlaybackService.Instance.Player);
            _poll.Start();
            if (_core.HasSavedLogin)
            {
                var context = _context;
                try { await ConnectExistingAsync(_core, context); }
                catch (Exception ex) { if (context == _context) ShowSetup(ex.Message); }
            }
        }

        async void Connect_Click(object sender, RoutedEventArgs e)
        {
            var context = ++_context;
            _operation++;
            ConnectButton.IsEnabled = false;
            SetupStatus.Text = "Connecting…";
            var client = new CoreClient();
            try
            {
                await client.LoginAsync(CoreUrlBox.Text, UsernameBox.Text, PasswordBox.Password);
                PasswordBox.Password = "";
                if (context != _context) return;
                _core = client;
                await ConnectExistingAsync(client, context);
            }
            catch (Exception ex) { if (context == _context) ShowSetup(ex.Message); }
            finally { if (context == _context) ConnectButton.IsEnabled = true; }
        }

        async Task ConnectExistingAsync(CoreClient client, long context)
        {
            await client.RequestAsync("/api/v1/health");
            var albums = await client.PublishedMusicAsync();
            if (context != _context) return;
            _albums = albums;
            try
            {
                if (_server == null) { _server = new EndpointServer(() => _core.DeviceToken); await _server.StartAsync(); }
                if (string.IsNullOrWhiteSpace(_server.Address)) throw new InvalidOperationException("No Xbox LAN address for local playback.");
                if (!client.HasDeviceCredential) await client.PairDeviceAsync(XboxIdentity.EndpointId);
                if (context != _context) return;
                await client.RegisterEndpointAsync(XboxIdentity.EndpointId, XboxIdentity.FriendlyName, _server.Address);
                _heartbeat.Start();
            }
            catch (Exception ex) { if (context == _context) Toast("Connected to Core; Xbox output pairing: " + ex.Message); }
            var outputs = await client.RequestAsync("/api/v1/endpoints");
            if (context != _context) return;
            SetOutputs(outputs);
            ConnectionText.Text = "R1 revision 29 · Core connected";
            CoreAddressText.Text = client.BaseUrl;
            SetupPanel.Visibility = Visibility.Collapsed;
            Shell.Visibility = Visibility.Visible;
            await NavigateAsync("Home");
        }

        void ShowSetup(string message)
        {
            Shell.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
            SetupStatus.Text = message;
            ConnectionText.Text = "Connect to your existing Core";
        }

        void SetOutputs(JsonObject root)
        {
            _updatingOutput = true;
            OutputPicker.Items.Clear();
            var ids = new HashSet<string>();
            if (root.ContainsKey("endpoints")) foreach (var value in root.GetNamedArray("endpoints"))
            {
                if (value.ValueType != JsonValueType.Object) continue;
                var row = value.GetObject(); var id = Text(row, "id");
                if (id.Length == 0 || !ids.Add(id) || (row.ContainsKey("online") && !row.GetNamedBoolean("online", true))) continue;
                OutputPicker.Items.Add(new ComboBoxItem { Content = Text(row, "name", id), Tag = id });
            }
            object saved;
            var preferred = _settings.Values.TryGetValue("xbox.control.preferredOutput:" + _core.BaseUrl, out saved) ? saved as string : "";
            var items = OutputPicker.Items.Cast<ComboBoxItem>().ToList();
            var selected = items.FirstOrDefault(i => (string)i.Tag == preferred) ??
                items.FirstOrDefault(i => (string)i.Tag == XboxIdentity.EndpointId) ?? items.FirstOrDefault();
            OutputPicker.SelectedItem = selected;
            _endpoint = selected?.Tag as string ?? "";
            _updatingOutput = false;
        }

        async void Output_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingOutput) return;
            _endpoint = (OutputPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            if (_endpoint.Length > 0) _settings.Values["xbox.control.preferredOutput:" + _core.BaseUrl] = _endpoint;
            await PollAsync();
        }

        async void Navigate_Click(object sender, RoutedEventArgs e) => await NavigateAsync((sender as Button)?.Tag as string ?? "Home");
        async void Refresh_Click(object sender, RoutedEventArgs e) => await NavigateAsync(_section, true);

        async Task NavigateAsync(string section, bool refresh = false)
        {
            if (!_core.HasSavedLogin) return;
            var context = _context; var operation = ++_operation; var client = _core;
            _section = section; _album = null;
            Heading.Text = section;
            PlayingPanel.Visibility = section == "Playing" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            Cards.Visibility = section == "Playing" || section == "Settings" ? Visibility.Collapsed : Visibility.Visible;
            AlbumPanel.Visibility = Visibility.Collapsed;
            VideoPanel.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Collapsed;
            Filters.Visibility = section == "Playing" || section == "Settings" ? Visibility.Collapsed : Visibility.Visible;
            CollectionPicker.Visibility = section == "Your Music" ? Visibility.Visible : Visibility.Collapsed;
            SpokenPicker.Visibility = section == "Spoken Word" ? Visibility.Visible : Visibility.Collapsed;
            BandPicker.Visibility = section == "Videos" ? Visibility.Visible : Visibility.Collapsed;
            _sectionCards = new List<MediaCard>();
            Cards.ItemsSource = null;
            try
            {
                if (section == "Playing") { await PollAsync(); return; }
                if (section == "Settings") return;
                if (section == "Home" || section == "Your Music")
                {
                    if (refresh) { var albums = await client.PublishedMusicAsync(); if (context != _context || operation != _operation) return; _albums = albums; }
                    var rows = section == "Home" ? _albums.Where(a => a.Collection == "albums").Take(16) :
                        _albums.Where(a => a.Collection == ((CollectionPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "albums"));
                    _sectionCards = rows.Select(a => new MediaCard { Id = a.Id, Kind = "album", Album = a,
                        Title = a.Title, Artist = a.Artist, Meta = a.Meta,
                        ArtworkPath = a.HasArtwork ? "/api/v1/library/catalogue/albums/" + Uri.EscapeDataString(a.Id) + "/artwork" : null }).ToList();
                }
                else if (section == "Streaming")
                {
                    var radio = await client.RadioAsync();
                    if (context != _context || operation != _operation) return;
                    _sectionCards = radio; Heading.Text = "Streaming · Radio · " + client.RadioSource;
                }
                else if (section == "Videos")
                {
                    var videos = await client.VideosAsync();
                    if (context != _context || operation != _operation) return;
                    _videos = videos;
                    var selected = BandPicker.SelectedItem as string;
                    BandPicker.ItemsSource = new[] { "All bands" }.Concat(videos.Select(v => v.Artist).Distinct()).ToList();
                    BandPicker.SelectedItem = selected != null && BandPicker.Items.Contains(selected) ? selected : "All bands";
                    _sectionCards = videos;
                }
                else if (section == "Spoken Word")
                {
                    var spoken = await client.SpokenAsync(SpokenPicker.SelectedIndex == 1);
                    if (context != _context || operation != _operation) return;
                    _sectionCards = spoken;
                }
                if (context != _context || operation != _operation) return;
                ApplyFilter();
                await LoadArtworkAsync(client, _sectionCards, context, operation);
            }
            catch (Exception ex) { if (context == _context && operation == _operation) { EmptyText.Text = ex.Message + " · Refresh to retry."; EmptyText.Visibility = Visibility.Visible; } }
        }

        async Task LoadArtworkAsync(CoreClient client, IEnumerable<MediaCard> cards, long context, long operation)
        {
            foreach (var card in cards)
            {
                if (context != _context || operation != _operation) return;
                try { await client.LoadArtworkAsync(card); } catch { /* A cover failure cannot delay playback or replace metadata. */ }
            }
        }

        void ApplyFilter()
        {
            var query = SearchBox?.Text ?? "";
            var band = BandPicker?.SelectedItem as string;
            var rows = _sectionCards.Where(c => (c.Title + " " + c.Artist + " " + c.Meta).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 &&
                (_section != "Videos" || band == null || band == "All bands" || c.Artist == band)).ToList();
            Cards.ItemsSource = rows;
            EmptyText.Text = "Nothing in this part of your Core library yet.";
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void Search_Changed(object sender, TextChangedEventArgs e) { if (Cards != null) ApplyFilter(); }
        async void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (Cards == null || !_initialised) return;
            if (sender == BandPicker) ApplyFilter(); else await NavigateAsync(_section);
        }

        async void Card_Click(object sender, ItemClickEventArgs e)
        {
            var card = e.ClickedItem as MediaCard;
            if (card == null) return;
            if (card.Kind == "video") { await PlayVideoAsync(card); return; }
            if (card.Kind == "album") { ShowAlbum(card.Album); return; }
            var client = _core; var endpoint = _endpoint; var context = _context;
            if (card.Kind == "podcast")
            {
                try { var episodes = await client.EpisodesAsync(card.Id); if (context != _context) return; _sectionCards = episodes; ApplyFilter(); }
                catch (Exception ex) { Toast(ex.Message); }
                return;
            }
            await CommandAsync(async () => {
                RequireEndpoint(endpoint);
                if (card.Kind == "radio") await client.PlayRadioAsync(card, endpoint);
                else if (card.Kind == "episode") await client.PlayEpisodeAsync(card, endpoint);
                else await client.PlayTracksAsync(endpoint, new[] { new CoreTrack { ItemId = card.Id, Title = card.Title } }, false);
            });
        }

        void ShowAlbum(CoreAlbum album)
        {
            _album = album;
            Cards.Visibility = Visibility.Collapsed; Filters.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Collapsed; AlbumPanel.Visibility = Visibility.Visible;
            AlbumTitle.Text = album.Artist + " · " + album.Title;
            EditionPicker.ItemsSource = album.Editions; EditionPicker.SelectedIndex = album.Editions.Count > 0 ? 0 : -1;
            Tracks.ItemsSource = SelectedTracks();
        }
        IList<CoreTrack> SelectedTracks() => (EditionPicker.SelectedItem as CoreEdition)?.Tracks ?? _album?.Tracks ?? new List<CoreTrack>();
        void Edition_Changed(object sender, SelectionChangedEventArgs e) { if (Tracks != null) Tracks.ItemsSource = SelectedTracks(); }
        async void AlbumBack_Click(object sender, RoutedEventArgs e) => await NavigateAsync(_section);
        async void PlayAlbum_Click(object sender, RoutedEventArgs e) => await PlayTracksAsync(SelectedTracks(), false);
        async void AppendAlbum_Click(object sender, RoutedEventArgs e) => await PlayTracksAsync(SelectedTracks(), true);
        async void Track_Click(object sender, ItemClickEventArgs e) => await PlayTracksAsync(new[] { (CoreTrack)e.ClickedItem }, false);
        async void TrackPlay_Click(object sender, RoutedEventArgs e) => await PlayTracksAsync(new[] { (CoreTrack)((Button)sender).Tag }, false);
        async void TrackAppend_Click(object sender, RoutedEventArgs e) => await PlayTracksAsync(new[] { (CoreTrack)((Button)sender).Tag }, true);
        async Task PlayTracksAsync(IList<CoreTrack> tracks, bool append)
        {
            var client = _core; var endpoint = _endpoint;
            await CommandAsync(async () => { RequireEndpoint(endpoint); await client.PlayTracksAsync(endpoint, tracks, append); });
            if (tracks.Count > 0 && !append) { FooterTitle.Text = tracks[0].Title + " · " + tracks[0].Artist; }
        }

        async Task PlayVideoAsync(MediaCard card)
        {
            if (_videoOpening || _videoId == card.Id) return;
            if (!card.Available || !card.Approved) { Toast("This video is not available for playback on Core."); return; }
            var context = _context; var operation = ++_operation; var client = _core;
            _videoOpening = true;
            try
            {
                var url = await client.VideoStreamAsync(card, CompatibleVideo.IsChecked != false);
                if (context != _context || operation != _operation) return;
                _videoId = card.Id;
                PlaybackService.Instance.PlayUrl(url, new JsonObject { ["title"] = JsonValue.CreateStringValue(card.Title),
                    ["artist"] = JsonValue.CreateStringValue(card.Artist), ["album"] = JsonValue.CreateStringValue(card.Meta ?? "") }, 0.7, 0);
                Cards.Visibility = Visibility.Collapsed; Filters.Visibility = Visibility.Collapsed;
                VideoPanel.Visibility = Visibility.Visible;
                VideoMetadata.Text = card.Artist + " · " + card.Title + "\n" + card.Meta;
            }
            catch (Exception ex) { if (context == _context) Toast("Stream unavailable: " + ex.Message); }
            finally { _videoOpening = false; }
        }
        async void VideoBack_Click(object sender, RoutedEventArgs e)
        {
            _operation++; _videoId = ""; PlaybackService.Instance.Stop(); await NavigateAsync("Videos");
        }

        async Task HeartbeatAsync()
        {
            var client = _core; var context = _context;
            if (_server == null || !client.HasDeviceCredential) return;
            try { await client.RegisterEndpointAsync(XboxIdentity.EndpointId, XboxIdentity.FriendlyName, _server.Address); }
            catch { if (context == _context) ConnectionText.Text = "Core connection interrupted"; }
        }
        async Task PollAsync()
        {
            if (_pollBusy || !_core.HasSavedLogin || _endpoint.Length == 0) return;
            _pollBusy = true;
            var context = _context; var endpoint = _endpoint; var client = _core;
            try
            {
                PlaybackSnapshot state;
                if (endpoint == XboxIdentity.EndpointId) state = PlaybackService.Instance.Snapshot();
                else
                {
                    var root = await client.RequestAsync("/api/v1/endpoints/" + Uri.EscapeDataString(endpoint) + "/status");
                    state = new PlaybackSnapshot { State = Text(root, "state"), Playing = root.GetNamedBoolean("playing", false),
                        Title = Text(root, "title"), Artist = Text(root, "artist"), Album = Text(root, "album"),
                        PositionSeconds = Number(root, "position_seconds"), DurationSeconds = Number(root, "duration_seconds"),
                        Volume = Number(root, "volume"), Muted = root.GetNamedBoolean("muted", false) };
                }
                if (context != _context || endpoint != _endpoint) return;
                _updatingPlayback = true;
                PlayingTitle.Text = string.IsNullOrWhiteSpace(state.Title) ? "Nothing playing" : state.Title;
                PlayingArtist.Text = string.Join(" · ", new[] { state.Artist, state.Album }.Where(v => !string.IsNullOrWhiteSpace(v)));
                PlayingState.Text = state.State;
                PlayButton.Content = state.Playing ? "Pause" : "Play"; PlayButton.Tag = state.Playing ? "pause" : "resume";
                Seek.Maximum = Math.Max(1, state.DurationSeconds); Seek.IsEnabled = state.DurationSeconds > 0;
                if (!_seekEditing) Seek.Value = Math.Max(0, Math.Min(Seek.Maximum, state.PositionSeconds));
                Volume.Value = Math.Max(0, Math.Min(100, state.Volume));
                TimeText.Text = Duration(state.PositionSeconds) + " / " + Duration(state.DurationSeconds);
                if (!string.IsNullOrWhiteSpace(state.Title)) FooterTitle.Text = state.Title + " · " + state.Artist;
                var cover = _sectionCards.FirstOrDefault(c => c.Title == state.Album);
                if (cover != null) PlayingArtwork.Source = cover.Artwork;
                _updatingPlayback = false;
                if (_section == "Playing")
                {
                    var queue = await client.QueueAsync(endpoint);
                    if (context == _context && endpoint == _endpoint) QueueList.ItemsSource = queue;
                }
            }
            catch (Exception ex) { if (_section == "Playing" && context == _context) PlayingState.Text = "Core status unavailable · " + ex.Message; }
            finally { _updatingPlayback = false; _pollBusy = false; }
        }

        async Task CommandAsync(Func<Task> action)
        {
            var context = _context;
            try { await action(); if (context == _context) await PollAsync(); }
            catch (Exception ex) { if (context == _context) Toast(ex.Message); }
        }
        static void RequireEndpoint(string id) { if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Choose a speaker first."); }
        async void Transport_Click(object sender, RoutedEventArgs e)
        {
            var client = _core; var endpoint = _endpoint; var action = (sender as Button)?.Tag as string;
            await CommandAsync(async () => { RequireEndpoint(endpoint); await client.ControlAsync(endpoint, action); });
        }
        async void FooterPlay_Click(object sender, RoutedEventArgs e)
        {
            var action = PlayButton.Tag as string ?? "resume"; var client = _core; var endpoint = _endpoint;
            await CommandAsync(async () => { RequireEndpoint(endpoint); await client.ControlAsync(endpoint, action); });
        }
        async void ClearQueue_Click(object sender, RoutedEventArgs e)
        {
            var client = _core; var endpoint = _endpoint;
            await CommandAsync(async () => { RequireEndpoint(endpoint); await client.ClearQueueAsync(endpoint); });
        }
        async void Seek_Changed(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_updatingPlayback || !_initialised || _endpoint.Length == 0) return;
            var op = ++_seekOperation; var context = _context; var client = _core; var endpoint = _endpoint; var value = e.NewValue;
            _seekEditing = true;
            await Task.Delay(400);
            if (op != _seekOperation || context != _context || endpoint != _endpoint) return;
            await CommandAsync(async () => await client.RequestAsync("/api/v1/playback/" + Uri.EscapeDataString(endpoint) +
                "/control/seek?position_seconds=" + value.ToString(System.Globalization.CultureInfo.InvariantCulture), "POST"));
            _seekEditing = false;
        }
        async void Volume_Changed(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_updatingPlayback || !_initialised || _endpoint.Length == 0) return;
            var op = ++_volumeOperation; var context = _context; var client = _core; var endpoint = _endpoint; var value = (int)e.NewValue;
            await Task.Delay(300);
            if (op != _volumeOperation || context != _context || endpoint != _endpoint) return;
            await CommandAsync(async () => await client.RequestAsync("/api/v1/playback/" + Uri.EscapeDataString(endpoint) + "/volume?volume=" + value, "POST"));
        }
        async void Mute_Click(object sender, RoutedEventArgs e)
        {
            var client = _core; var endpoint = _endpoint;
            var mute = !(PlaybackService.Instance.Snapshot().Muted);
            await CommandAsync(async () => { RequireEndpoint(endpoint); await client.RequestAsync("/api/v1/playback/" + Uri.EscapeDataString(endpoint) + "/mute?muted=" + mute.ToString().ToLowerInvariant(), "POST"); });
        }
        void Forget_Click(object sender, RoutedEventArgs e)
        {
            _context++; _operation++; _heartbeat.Stop(); _core.Forget();
            _albums.Clear(); _videos.Clear(); _sectionCards.Clear(); _endpoint = ""; _videoId = "";
            Cards.ItemsSource = null; QueueList.ItemsSource = null; VideoPanel.Visibility = Visibility.Collapsed;
            PlaybackService.Instance.Stop(); ShowSetup("");
        }
        async void Page_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.GamepadView) { e.Handled = true; await NavigateAsync("Playing"); }
            else if (e.Key == VirtualKey.GamepadMenu) { e.Handled = true; await NavigateAsync("Settings"); }
            else if (e.Key == VirtualKey.GamepadB || e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                if (VideoPanel.Visibility == Visibility.Visible) VideoBack_Click(sender, new RoutedEventArgs());
                else await NavigateAsync(_album != null ? "Your Music" : "Home");
            }
        }
        void Toast(string text) { ToastText.Text = text; ToastBorder.Visibility = Visibility.Visible; _toast.Stop(); _toast.Start(); }
        static string Text(JsonObject obj, string key, string fallback = "") => obj.ContainsKey(key) && obj[key].ValueType == JsonValueType.String ? obj[key].GetString() : fallback;
        static double Number(JsonObject obj, string key) => obj.ContainsKey(key) && obj[key].ValueType == JsonValueType.Number ? obj[key].GetNumber() : 0;
        static string Duration(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    }
}
