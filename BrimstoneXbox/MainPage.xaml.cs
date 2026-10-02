using BrimstoneXbox.Models;
using BrimstoneXbox.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BrimstoneXbox
{
    public sealed partial class MainPage : Page
    {
        const string EditionSetting = "xboxEdition";
        const string SooloosHostSetting = "sooloosHost";

        readonly CoreClient _core = new CoreClient();
        readonly XboxCoreRuntime _nativeCore = new XboxCoreRuntime();
        readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
        readonly DispatcherTimer _heartbeat = new DispatcherTimer();
        readonly DispatcherTimer _uiRefresh = new DispatcherTimer();
        readonly DispatcherTimer _serviceRefresh = new DispatcherTimer();
        readonly DispatcherTimer _toastTimer = new DispatcherTimer();

        EndpointServer _server;
        SooloosClient _sooloos;
        CoreAlbum _album;
        CoreAlbum _heroAlbum;
        List<CoreAlbum> _albums = new List<CoreAlbum>();
        SooloosZone _currentZone;
        string _mode = "";
        string _sooloosZoneId = "";
        bool _serviceRefreshBusy;
        string _lastObservedRipFingerprint = "";

        public MainPage()
        {
            InitializeComponent();
            Loaded += MainPage_Loaded;

            PlaybackService.Instance.StateChanged += (s, e) =>
                Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, UpdatePlaybackUi);

            _heartbeat.Interval = TimeSpan.FromSeconds(25);
            _heartbeat.Tick += Heartbeat_Tick;

            _uiRefresh.Interval = TimeSpan.FromSeconds(1);
            _uiRefresh.Tick += (s, e) => UpdatePlaybackUi();
            _uiRefresh.Start();

            _serviceRefresh.Interval = TimeSpan.FromSeconds(5);
            _serviceRefresh.Tick += ServiceRefresh_Tick;

            _toastTimer.Interval = TimeSpan.FromSeconds(4);
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                ToastBorder.Visibility = Visibility.Collapsed;
            };
        }

        async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            CoreUrlBox.Text = string.IsNullOrWhiteSpace(_core.BaseUrl)
                ? "http://10.26.30.20:8080"
                : _core.BaseUrl;
            SooloosHostBox.Text = ReadSetting(SooloosHostSetting);

            var saved = ReadSetting(EditionSetting).ToLowerInvariant();
            if (saved != "core" && saved != "sooloos")
            {
                ShowFirstRun();
                return;
            }

            _mode = saved;
            ConfigureEdition();

            if (_mode == "core")
            {
                try
                {
                    await _nativeCore.StartAsync("core");
                    await LoadLibrary();
                    ShowShell();
                    ConnectionText.Text = _nativeCore.Ready
                        ? "Xbox Core ready"
                        : "Xbox Core starting";
                }
                catch (Exception ex)
                {
                    ShowShell();
                    ConnectionText.Text = "Xbox Core degraded";
                    Toast("Xbox Core: " + ex.Message);
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(SooloosHostBox.Text))
                {
                    ShowSetup();
                    return;
                }

                try
                {
                    await ConnectSooloos();
                    await LoadLibrary();
                    ShowShell();
                }
                catch (Exception ex)
                {
                    SetupStatusText.Text = ex.Message;
                    ShowSetup();
                }
            }
        }

        void CoreEditionButton_Click(object sender, RoutedEventArgs e)
        {
            SelectEdition("core");
        }

        void SooloosEditionButton_Click(object sender, RoutedEventArgs e)
        {
            SelectEdition("sooloos");
        }

        async void SelectEdition(string mode)
        {
            _mode = mode;
            WriteSetting(EditionSetting, mode);
            ConfigureEdition();

            if (_mode == "core")
            {
                try
                {
                    await _nativeCore.StartAsync("core");
                    await LoadLibrary();
                    ShowShell();
                    ConnectionText.Text = _nativeCore.Ready
                        ? "Xbox Core ready"
                        : "Xbox Core starting";
                }
                catch (Exception ex)
                {
                    ShowShell();
                    ConnectionText.Text = "Xbox Core degraded";
                    Toast("Xbox Core: " + ex.Message);
                }
                return;
            }

            ShowSetup();
        }

        void ConfigureEdition()
        {
            var core = _mode == "core";
            CoreSetupFields.Visibility = core ? Visibility.Visible : Visibility.Collapsed;
            SooloosSetupFields.Visibility = core ? Visibility.Collapsed : Visibility.Visible;
            SetupHeadingText.Text = core ? "Optional Core Bridge" : "Connect Sooloos";
            SetupHelpText.Text = core
                ? "This Xbox already runs Brimstone Core locally. Use this only to attach an existing Core during migration or testing."
                : "Connect directly to the Meridian/Sooloos Core on this network.";
            SetupConnectButton.Content = core ? "Connect bridge" : "Connect Sooloos";

            EditionBadgeText.Text = core ? "CORE · XBOX" : "SOOLOOS · XBOX";
            SettingsEditionText.Text = core ? "Core" : "Sooloos";
            FooterSignalText.Text = core ? "Brimstone audio" : "Meridian / Sooloos";
        }

        async void SetupConnectButton_Click(object sender, RoutedEventArgs e)
        {
            SetupConnectButton.IsEnabled = false;
            SetupStatusText.Text = "";

            try
            {
                if (_mode == "core")
                {
                    SetupStatusText.Text = "Starting Xbox Core…";
                    await _nativeCore.StartAsync("core");
                    SetupStatusText.Text = "Signing in…";
                    await _core.LoginAsync(CoreUrlBox.Text, UsernameBox.Text, PasswordBox.Password);
                    SetupStatusText.Text = "Pairing Xbox…";
                    await BringCoreOnline(true);
                    PasswordBox.Password = "";
                }
                else
                {
                    WriteSetting(SooloosHostSetting, SooloosHostBox.Text);
                    SetupStatusText.Text = "Connecting to Sooloos…";
                    await ConnectSooloos();
                }

                SetupStatusText.Text = "Loading Your Music…";
                await LoadLibrary();
                SetupStatusText.Text = "";
                ShowShell();
                Toast(_mode == "core" ? "Brimstone Core connected" : "Sooloos connected");
            }
            catch (Exception ex)
            {
                SetupStatusText.Text = ex.Message;
                Toast(ex.Message);
            }
            finally
            {
                SetupConnectButton.IsEnabled = true;
            }
        }

        void SetupBackButton_Click(object sender, RoutedEventArgs e)
        {
            ShowFirstRun();
        }

        async Task BringCoreOnline(bool allowPair)
        {
            if (_server == null)
            {
                _server = new EndpointServer(() => _core.DeviceToken);
                await _server.StartAsync();
            }

            if (string.IsNullOrWhiteSpace(_server.Address))
                throw new InvalidOperationException("Xbox has no usable LAN address.");

            if (!_core.HasDeviceCredential)
            {
                if (!allowPair) throw new InvalidOperationException("Xbox is not paired.");
                await _core.PairDeviceAsync(XboxIdentity.EndpointId);
            }

            await _core.RegisterEndpointAsync(XboxIdentity.EndpointId, XboxIdentity.FriendlyName, _server.Address);
            ConnectionText.Text = "Core connected";
            OutputText.Text = "Xbox";
            FooterOutputText.Text = "Xbox";
            _heartbeat.Start();
            _serviceRefresh.Start();
            UpdateSettings();
        }

        async Task ConnectSooloos()
        {
            var host = SooloosClient.NormaliseHost(SooloosHostBox.Text);
            if (string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("Enter the Sooloos Core address.");

            _sooloos = new SooloosClient(host);
            var zones = await _sooloos.ZonesAsync();
            if (zones.Count == 0)
                throw new InvalidOperationException("Sooloos connected, but no zones were returned.");

            _sooloosZoneId = zones[0].Id;
            _currentZone = zones[0];
            WriteSetting(SooloosHostSetting, host);
            SooloosHostBox.Text = host;
            ConnectionText.Text = "Sooloos connected";
            OutputText.Text = _currentZone.Name;
            FooterOutputText.Text = _currentZone.Name;
            _heartbeat.Stop();
            _serviceRefresh.Start();
            UpdateSettings();
        }

        async void Heartbeat_Tick(object sender, object e)
        {
            if (_mode != "core" || _server == null || !_core.HasDeviceCredential) return;
            try
            {
                await _core.RegisterEndpointAsync(XboxIdentity.EndpointId, XboxIdentity.FriendlyName, _server.Address);
                ConnectionText.Text = "Core connected";
            }
            catch
            {
                ConnectionText.Text = "Core interrupted";
            }
        }

        async void ServiceRefresh_Tick(object sender, object e)
        {
            if (_serviceRefreshBusy) return;
            _serviceRefreshBusy = true;
            try
            {
                if (_mode == "sooloos")
                    await RefreshSooloosState();
                else if (_mode == "core")
                {
                    if (QueuePanel.Visibility == Visibility.Visible)
                        await RefreshQueue();
                    if (RipPanel.Visibility == Visibility.Visible)
                        await RefreshRipStatus();

                    await ObserveCompletedRipAsync();
                }
            }
            catch
            {
                // Keep TV playback usable during a transient discovery/API failure.
            }
            finally
            {
                _serviceRefreshBusy = false;
            }
        }

        async Task LoadLibrary()
        {
            List<CoreAlbum> albums;
            if (_mode == "sooloos")
            {
                if (_sooloos == null) await ConnectSooloos();
                albums = await _sooloos.SearchAlbumsAsync();
                LibrarySummaryText.Text = albums.Count + " albums · " +
                    (_currentZone == null ? "Sooloos" : _currentZone.Name);
            }
            else
            {
                albums = await _nativeCore.GetAlbumsAsync();
                LibrarySummaryText.Text = albums.Count +
                    (albums.Count == 1 ? " album" : " albums") +
                    " · Xbox Core";
            }

            _albums = albums ?? new List<CoreAlbum>();

            var alphabetical = _albums
                .OrderBy(a => a.Artist ?? "")
                .ThenBy(a => a.Title ?? "")
                .ToList();

            var recent = _albums
                .OrderByDescending(a => a.AddedAt)
                .ThenBy(a => a.Title ?? "")
                .ToList();

            AlbumGrid.ItemsSource = alphabetical;
            RecentlyAddedGrid.ItemsSource = recent.Take(14).ToList();
            RecentlyRippedGrid.ItemsSource = recent.Take(10).ToList();

            var favourites = _albums
                .Where(a => a.IsFavourite)
                .OrderBy(a => a.Artist ?? "")
                .ThenBy(a => a.Title ?? "")
                .ToList();

            FavouritesGrid.ItemsSource = favourites;
            FavouriteSection.Visibility = favourites.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            RefreshHomeHero();
        }

        void RefreshHomeHero()
        {
            if (_mode == "sooloos")
            {
                ContinueSection.Visibility = Visibility.Collapsed;
                var first = _albums.FirstOrDefault();
                SetHero(first, "YOUR MUSIC");
                return;
            }

            var playback = PlaybackService.Instance.Snapshot();
            CoreAlbum active = null;
            if (!string.IsNullOrWhiteSpace(playback.Album))
            {
                active = _albums.FirstOrDefault(a =>
                    string.Equals(a.Title, playback.Album,
                        StringComparison.OrdinalIgnoreCase));
            }

            if (active != null &&
                !string.Equals(playback.State, "stopped",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(playback.State, "idle",
                    StringComparison.OrdinalIgnoreCase))
            {
                ContinueGrid.ItemsSource = new List<CoreAlbum> { active };
                ContinueSection.Visibility = Visibility.Visible;
                SetHero(active, playback.Playing ? "NOW PLAYING" : "CONTINUE LISTENING");
                HeroPlayButton.Content = playback.Playing ? "Ⅱ  Pause" : "▶  Resume";
                return;
            }

            ContinueSection.Visibility = Visibility.Collapsed;
            var newest = _albums
                .OrderByDescending(a => a.AddedAt)
                .FirstOrDefault();
            SetHero(newest, newest == null ? "BRIMSTONE CORE" : "RECENTLY ADDED");
            HeroPlayButton.Content = newest == null ? "▶  Play" : "▶  Play album";
        }

        void SetHero(CoreAlbum album, string eyebrow)
        {
            _heroAlbum = album;
            HeroEyebrowText.Text = eyebrow ?? "BRIMSTONE CORE";

            if (album == null)
            {
                HeroTitleText.Text = "Your Music";
                HeroArtistText.Text = "Rip a CD or add music to get started";
                HeroArtworkImage.Source = null;
                HeroPlayButton.IsEnabled = false;
                return;
            }

            HeroTitleText.Text = album.Title ?? "Untitled";
            HeroArtistText.Text = album.Artist ?? "Unknown Artist";
            HeroArtworkImage.Source = album.Artwork;
            HeroPlayButton.IsEnabled = true;
        }

        void AlbumGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            _album = e.ClickedItem as CoreAlbum;
            if (_album == null) return;

            AlbumTitleText.Text = _album.Title;
            AlbumArtistText.Text = _album.Artist;
            AlbumArtworkImage.Source = _album.Artwork;
            FavouriteAlbumButton.Content = _album.IsFavourite
                ? "♥  Favourite"
                : "♡  Favourite";

            if (_mode == "core")
            {
                TracksList.Visibility = Visibility.Visible;
                TracksList.ItemsSource = _album.Tracks;
            }
            else
            {
                TracksList.Visibility = Visibility.Collapsed;
                TracksList.ItemsSource = null;
            }

            ShowContent(AlbumPanel);
            PlayAlbumButton.Focus(FocusState.Programmatic);
        }

        async void HeroPlayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var playback = PlaybackService.Instance.Snapshot();
                if (_mode == "core" &&
                    _heroAlbum != null &&
                    string.Equals(playback.Album, _heroAlbum.Title,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(playback.State, "stopped",
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(playback.State, "idle",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await Transport(playback.Playing ? "pause" : "resume");
                }
                else if (_heroAlbum != null)
                {
                    if (_mode == "sooloos")
                    {
                        _album = _heroAlbum;
                        await _sooloos.PlayAlbumAsync(_sooloosZoneId, _heroAlbum.Id);
                    }
                    else
                    {
                        await _nativeCore.PlayAlbumAsync(_heroAlbum);
                    }
                }

                RefreshHomeHero();
            }
            catch (Exception ex)
            {
                Toast(ex.Message);
            }
        }

        async void HeroRipButton_Click(object sender, RoutedEventArgs e)
        {
            ShowContent(RipPanel);
            await RefreshRipStatus();
        }

        async void FavouriteAlbumButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mode != "core" || _album == null)
                return;

            var favourite = _nativeCore.ToggleFavourite(_album);
            FavouriteAlbumButton.Content = favourite
                ? "♥  Favourite"
                : "♡  Favourite";

            await LoadLibrary();
            Toast(favourite ? "Added to Favourites" : "Removed from Favourites");
        }

        async void TracksList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_mode != "core") return;
            var track = e.ClickedItem as CoreTrack;
            if (track == null) return;

            try
            {
                await _nativeCore.PlayTrackAsync(track);
                ShowContent(NowPlayingPanel);
            }
            catch (Exception ex)
            {
                Toast(ex.Message);
            }
        }

        async void PlayAlbumButton_Click(object sender, RoutedEventArgs e)
        {
            if (_album == null) return;

            try
            {
                if (_mode == "sooloos")
                {
                    if (_sooloos == null || string.IsNullOrWhiteSpace(_sooloosZoneId))
                        throw new InvalidOperationException("No Sooloos zone is selected.");
                    await _sooloos.PlayAlbumAsync(_sooloosZoneId, _album.Id);
                    await RefreshSooloosState();
                }
                else
                {
                    await _nativeCore.PlayAlbumAsync(_album);
                }
                ShowContent(NowPlayingPanel);
            }
            catch (Exception ex)
            {
                Toast(ex.Message);
            }
        }

        async Task Transport(string action)
        {
            try
            {
                if (_mode == "sooloos")
                {
                    if (_sooloos == null || string.IsNullOrWhiteSpace(_sooloosZoneId))
                        throw new InvalidOperationException("No Sooloos zone is selected.");
                    await _sooloos.ControlAsync(_sooloosZoneId, action);
                    await RefreshSooloosState();
                }
                else
                {
                    _nativeCore.Control(action);
                }
            }
            catch (Exception ex)
            {
                Toast(ex.Message);
            }
        }

        async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            var playing = _mode == "sooloos"
                ? _currentZone != null && (_currentZone.State ?? "").IndexOf("play", StringComparison.OrdinalIgnoreCase) >= 0
                : PlaybackService.Instance.Snapshot().Playing;
            await Transport(playing ? "pause" : "resume");
        }

        async void StopButton_Click(object sender, RoutedEventArgs e) => await Transport("stop");
        async void PreviousButton_Click(object sender, RoutedEventArgs e) => await Transport("previous");
        async void NextButton_Click(object sender, RoutedEventArgs e) => await Transport("next");

        void MusicNavButton_Click(object sender, RoutedEventArgs e) => ShowContent(MusicPanel);

        async void QueueNavButton_Click(object sender, RoutedEventArgs e)
        {
            ShowContent(QueuePanel);
            await RefreshQueue();
        }

        void NowNavButton_Click(object sender, RoutedEventArgs e) => ShowContent(NowPlayingPanel);

        async void RipNavButton_Click(object sender, RoutedEventArgs e)
        {
            ShowContent(RipPanel);
            await RefreshRipStatus();
        }

        void BackToLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            ShowContent(MusicPanel);
            MusicNavButton.Focus(FocusState.Programmatic);
        }

        void SettingsNavButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateSettings();
            ShowContent(SettingsPanel);
        }

        async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadLibrary();
                Toast("Library refreshed");
            }
            catch (Exception ex)
            {
                Toast(ex.Message);
            }
        }

        async Task RefreshQueue()
        {
            if (_mode == "sooloos")
            {
                await RefreshSooloosState();
                QueueList.ItemsSource = new List<QueueItem>();
                QueueSummaryText.Text = _currentZone == null
                    ? "Sooloos queue"
                    : _currentZone.Name + " · " + (_currentZone.Title ?? "Ready");
                return;
            }

            try
            {
                var queue = _nativeCore.GetQueue();
                QueueList.ItemsSource = queue;
                QueueSummaryText.Text = queue.Count == 0
                    ? "Nothing queued"
                    : queue.Count + (queue.Count == 1 ? " item" : " items") + " · Xbox";
            }
            catch (Exception ex)
            {
                QueueSummaryText.Text = ex.Message;
            }
        }

        async Task RefreshSooloosState()
        {
            if (_sooloos == null) return;
            var zones = await _sooloos.ZonesAsync();
            if (zones.Count == 0) return;

            _currentZone = zones.FirstOrDefault(z => z.Id == _sooloosZoneId) ?? zones[0];
            _sooloosZoneId = _currentZone.Id;
            OutputText.Text = _currentZone.Name;
            FooterOutputText.Text = _currentZone.Name;
            ConnectionText.Text = "Sooloos connected";
            UpdatePlaybackUi();
        }

        async Task ObserveCompletedRipAsync()
        {
            var status = await _nativeCore.GetRipStatusAsync();
            if (!string.Equals(
                JsonString(status, "state", "idle"),
                "complete",
                StringComparison.OrdinalIgnoreCase))
                return;

            var fingerprint = JsonString(status, "fingerprint", "");
            if (string.IsNullOrWhiteSpace(fingerprint) ||
                string.Equals(
                    fingerprint,
                    _lastObservedRipFingerprint,
                    StringComparison.Ordinal))
                return;

            _lastObservedRipFingerprint = fingerprint;
            await LoadLibrary();
            Toast("New album added to Your Music");
        }

        async Task RefreshRipStatus()
        {
            if (_mode == "sooloos")
            {
                RipModeText.Text = "AUTO-RIP READY";
                RipStatusText.Text = "Sooloos ingest is not armed yet";
                RipDetailText.Text = "The TV flow is locked: insert disc → identify → rip → verify → library.";
                return;
            }

            try
            {
                var status = await _nativeCore.GetRipStatusAsync();
                var state = JsonString(status, "state", "idle");

                RipModeText.Text = "XBOX AUTO-RIP";

                if (state == "ripping")
                {
                    var progress = JsonNumber(status, "progress_percent", 0);
                    var track = JsonNumber(status, "current_track", 0);
                    var total = JsonNumber(status, "track_count", 0);
                    RipStatusText.Text = "Ripping track " + track.ToString("0") +
                        " of " + total.ToString("0") +
                        " · " + progress.ToString("0.0") + "%";
                    RipDetailText.Text =
                        "Reading raw CDDA from the Xbox optical drive, writing WAV, hashing each track and publishing into the local Core library.";
                    return;
                }

                if (state == "complete")
                {
                    var tracks = JsonNumber(status, "completed_tracks",
                        JsonNumber(status, "track_count", 0));
                    var speed = JsonNumber(status, "rip_speed_x", 0);
                    var ejected = JsonBool(status, "eject");
                    RipStatusText.Text = "Rip complete · " + tracks.ToString("0") + " tracks";
                    RipDetailText.Text =
                        "Verified local rip" +
                        (speed > 0 ? " · " + speed.ToString("0.00") + "×" : "") +
                        (ejected ? " · disc ejected" : "") +
                        ". Refresh Your Music to see the album.";
                    return;
                }

                var probe = await _nativeCore.ProbeOpticalAsync();
                var probeState = JsonString(probe, "status", "unknown");
                var devices = JsonNumber(probe, "device_count", 0);

                RipStatusText.Text = devices > 0
                    ? "Waiting for a music disc"
                    : "Optical drive unavailable";
                RipDetailText.Text = devices > 0
                    ? "Xbox optical ingest is armed. Insert a CD and Brimstone will rip it into this Xbox Core."
                    : "Optical probe: " + probeState;
            }
            catch (Exception ex)
            {
                RipStatusText.Text = "Rip status unavailable";
                RipDetailText.Text = ex.Message;
            }
        }

        void RipNowButton_Click(object sender, RoutedEventArgs e)
        {
            _ = _nativeCore.RipNowAsync();
            Toast("Xbox Core rip started");
        }

        void ChangeEditionButton_Click(object sender, RoutedEventArgs e)
        {
            _heartbeat.Stop();
            _serviceRefresh.Stop();
            _mode = "";
            _settings.Values.Remove(EditionSetting);
            ShowFirstRun();
        }

        void ReconnectButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSetup();
        }

        void Page_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (ShellPanel.Visibility == Visibility.Visible)
            {
                if (e.Key == VirtualKey.GamepadView)
                {
                    ShowContent(NowPlayingPanel);
                    e.Handled = true;
                }
                else if (e.Key == VirtualKey.GamepadMenu)
                {
                    UpdateSettings();
                    ShowContent(SettingsPanel);
                    e.Handled = true;
                }
                else if (e.Key == VirtualKey.GamepadB && AlbumPanel.Visibility == Visibility.Visible)
                {
                    ShowContent(MusicPanel);
                    e.Handled = true;
                }
            }
            else if (SetupPanel.Visibility == Visibility.Visible && e.Key == VirtualKey.GamepadB)
            {
                ShowFirstRun();
                e.Handled = true;
            }
        }

        void UpdatePlaybackUi()
        {
            string title;
            string artist;
            string album;
            bool playing;
            string state;

            if (_mode == "sooloos" && _currentZone != null)
            {
                title = string.IsNullOrWhiteSpace(_currentZone.Title) ? "Nothing playing" : _currentZone.Title;
                artist = _currentZone.Subtitle ?? "";
                album = "";
                state = _currentZone.State ?? "";
                playing = state.IndexOf("play", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            else
            {
                var p = PlaybackService.Instance.Snapshot();
                title = string.IsNullOrWhiteSpace(p.Title) ? "Nothing playing" : p.Title;
                artist = p.Artist ?? "";
                album = p.Album ?? "";
                state = p.State ?? "";
                playing = p.Playing;
            }

            NowTitleText.Text = title;
            NowArtistText.Text = artist;
            NowAlbumText.Text = album;
            FooterTitleText.Text = title;
            FooterMetaText.Text = string.Join(" · ", new[] { artist, album, state }.Where(v => !string.IsNullOrWhiteSpace(v)));
            PlayPauseButton.Content = playing ? "Ⅱ  Pause" : "▶  Play";
            FooterPlayPauseButton.Content = playing ? "Ⅱ" : "▶";

            if (_mode == "core")
            {
                OutputText.Text = PlaybackService.Instance.AudioOutputName;
                FooterOutputText.Text = PlaybackService.Instance.AudioOutputName;

                if (MusicPanel.Visibility == Visibility.Visible)
                    RefreshHomeHero();
            }
        }

        void UpdateSettings()
        {
            SettingsEditionText.Text = _mode == "sooloos" ? "Sooloos" : "Core";
            SettingsCoreText.Text = _mode == "sooloos"
                ? (string.IsNullOrWhiteSpace(ReadSetting(SooloosHostSetting)) ? "Sooloos not configured" : ReadSetting(SooloosHostSetting))
                : (_nativeCore.Running
                    ? "Xbox Core · " + (_nativeCore.ApiAddress ?? "local API starting") +
                      (string.IsNullOrWhiteSpace(_core.BaseUrl) ? "" : "\nLibrary bridge · " + _core.BaseUrl)
                    : (string.IsNullOrWhiteSpace(_core.BaseUrl) ? "Core not configured" : _core.BaseUrl));
            SettingsEndpointText.Text = _mode == "sooloos"
                ? (_currentZone == null ? "No Sooloos zone" : _currentZone.Name)
                : XboxIdentity.EndpointId;
        }

        void ShowFirstRun()
        {
            FirstRunPanel.Visibility = Visibility.Visible;
            SetupPanel.Visibility = Visibility.Collapsed;
            ShellPanel.Visibility = Visibility.Collapsed;
            CoreEditionButton.Focus(FocusState.Programmatic);
        }

        void ShowSetup()
        {
            FirstRunPanel.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
            ShellPanel.Visibility = Visibility.Collapsed;
            ConfigureEdition();

            if (_mode == "sooloos")
            {
                SooloosHostBox.Text = ReadSetting(SooloosHostSetting);
                SooloosHostBox.Focus(FocusState.Programmatic);
            }
            else
            {
                CoreUrlBox.Text = string.IsNullOrWhiteSpace(_core.BaseUrl)
                    ? "http://10.26.30.20:8080"
                    : _core.BaseUrl;
                CoreUrlBox.Focus(FocusState.Programmatic);
            }
        }

        void ShowShell()
        {
            FirstRunPanel.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Collapsed;
            ShellPanel.Visibility = Visibility.Visible;
            ConfigureEdition();
            UpdateSettings();
            UpdatePlaybackUi();
            ShowContent(MusicPanel);
            _serviceRefresh.Start();
            if (_mode == "core")
            {
                ConnectionText.Text = _nativeCore.Ready
                    ? "Xbox Core ready"
                    : "Xbox Core starting";
                OutputText.Text = PlaybackService.Instance.AudioOutputName;
                FooterOutputText.Text = PlaybackService.Instance.AudioOutputName;
            }
            MusicNavButton.Focus(FocusState.Programmatic);
        }

        void ShowContent(UIElement panel)
        {
            MusicPanel.Visibility = AlbumPanel.Visibility = QueuePanel.Visibility =
                NowPlayingPanel.Visibility = RipPanel.Visibility = SettingsPanel.Visibility = Visibility.Collapsed;
            panel.Visibility = Visibility.Visible;
        }

        void Toast(string message)
        {
            ToastText.Text = message ?? "";
            ToastBorder.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        string ReadSetting(string key)
        {
            object value;
            return _settings.Values.TryGetValue(key, out value) ? value as string ?? "" : "";
        }

        static string JsonString(Windows.Data.Json.JsonObject obj, string key, string fallback = "")
        {
            return obj != null && obj.ContainsKey(key) &&
                   obj[key].ValueType == Windows.Data.Json.JsonValueType.String
                ? obj[key].GetString()
                : fallback;
        }

        static double JsonNumber(Windows.Data.Json.JsonObject obj, string key, double fallback)
        {
            return obj != null && obj.ContainsKey(key) &&
                   obj[key].ValueType == Windows.Data.Json.JsonValueType.Number
                ? obj[key].GetNumber()
                : fallback;
        }

        static bool JsonBool(Windows.Data.Json.JsonObject obj, string key)
        {
            return obj != null && obj.ContainsKey(key) &&
                   obj[key].ValueType == Windows.Data.Json.JsonValueType.Boolean &&
                   obj[key].GetBoolean();
        }

        void WriteSetting(string key, string value)
        {
            _settings.Values[key] = value ?? "";
        }
    }
}
