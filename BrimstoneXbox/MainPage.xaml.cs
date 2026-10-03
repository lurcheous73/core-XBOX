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

        readonly CoreClient _core = new CoreClient();
        readonly XboxCoreRuntime _nativeCore = new XboxCoreRuntime();
        readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
        readonly DispatcherTimer _heartbeat = new DispatcherTimer();
        readonly DispatcherTimer _uiRefresh = new DispatcherTimer();
        readonly DispatcherTimer _serviceRefresh = new DispatcherTimer();
        readonly DispatcherTimer _toastTimer = new DispatcherTimer();

        EndpointServer _server;
        CoreAlbum _album;
        CoreAlbum _heroAlbum;
        List<CoreAlbum> _albums = new List<CoreAlbum>();
        string _mode = "";
        bool _serviceRefreshBusy;
        string _lastObservedRipFingerprint = "";
        string _lastObservedBluRayManifest = "";

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
            _mode = "standalone";
            WriteSetting(EditionSetting, "standalone");
            _settings.Values.Remove("sooloosHost");

            CoreUrlBox.Text = string.IsNullOrWhiteSpace(_core.BaseUrl)
                ? ""
                : _core.BaseUrl;
            TransferCoreUrlBox.Text = _core.BaseUrl ?? "";
            TransferCoreUsernameBox.Text = "admin";

            ConfigureEdition();

            try
            {
                await _nativeCore.StartAsync("standalone");
                await LoadLibrary();
                ShowShell();
                _serviceRefresh.Start();
                ConnectionText.Text = _nativeCore.Ready
                    ? "Standalone Xbox Core ready"
                    : "Standalone Xbox Core starting";
            }
            catch (Exception ex)
            {
                ShowShell();
                _serviceRefresh.Start();
                ConnectionText.Text = "Xbox Core degraded";
                Toast("Xbox Core: " + ex.Message);
            }
        }

        void CoreEditionButton_Click(object sender, RoutedEventArgs e)
        {
            SelectEdition("standalone");
        }

        async void SelectEdition(string mode)
        {
            _mode = "standalone";
            WriteSetting(EditionSetting, "standalone");
            ConfigureEdition();

            try
            {
                await _nativeCore.StartAsync("standalone");
                await LoadLibrary();
                ShowShell();
                _serviceRefresh.Start();
                ConnectionText.Text = "Standalone Xbox Core ready";
            }
            catch (Exception ex)
            {
                ShowShell();
                _serviceRefresh.Start();
                ConnectionText.Text = "Xbox Core degraded";
                Toast("Xbox Core: " + ex.Message);
            }
        }

        void ConfigureEdition()
        {
            CoreSetupFields.Visibility = Visibility.Visible;
            SetupHeadingText.Text = "Optional Core destination";
            SetupHelpText.Text =
                "This Xbox is its own Brimstone Core. Connect another Core only when you want to copy music between them.";
            SetupConnectButton.Content = "Connect other Core";

            EditionBadgeText.Text = "STANDALONE CORE · XBOX";
            SettingsEditionText.Text = "Standalone Core";
            FooterSignalText.Text = "xbox-core.local";
        }

        async void SetupConnectButton_Click(object sender, RoutedEventArgs e)
        {
            SetupConnectButton.IsEnabled = false;
            SetupStatusText.Text = "";

            try
            {
                SetupStatusText.Text = "Connecting destination Core…";
                await _core.LoginAsync(
                    CoreUrlBox.Text,
                    UsernameBox.Text,
                    PasswordBox.Password);
                PasswordBox.Password = "";
                SetupStatusText.Text = "Destination Core ready";
                UpdateSettings();
                ShowShell();
                Toast("Destination Core connected");
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

        async void Heartbeat_Tick(object sender, object e)
        {
            if (_server == null || !_core.HasDeviceCredential) return;
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
                if (QueuePanel.Visibility == Visibility.Visible)
                    await RefreshQueue();
                if (RipPanel.Visibility == Visibility.Visible)
                    await RefreshRipStatus();

                await ObserveCompletedRipAsync();
            }
            catch
            {
                // Keep TV playback usable during a transient local service failure.
            }
            finally
            {
                _serviceRefreshBusy = false;
            }
        }

        async Task LoadLibrary()
        {
            var albums = await _nativeCore.GetAlbumsAsync();
            LibrarySummaryText.Text = albums.Count +
                (albums.Count == 1 ? " album" : " albums") +
                " · Standalone Xbox Core";

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

            var directTransfer =
                !string.Equals(
                    _album.SourceKind,
                    "bluray",
                    StringComparison.OrdinalIgnoreCase);
            SendToCoreButton.Visibility = directTransfer
                ? Visibility.Visible
                : Visibility.Collapsed;

            TracksList.Visibility = Visibility.Visible;
            TracksList.ItemsSource = _album.Tracks;

            ShowContent(AlbumPanel);
            PlayAlbumButton.Focus(FocusState.Programmatic);
        }

        async void HeroPlayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var playback = PlaybackService.Instance.Snapshot();
                if (_heroAlbum != null &&
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
                    await _nativeCore.PlayAlbumAsync(_heroAlbum);
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
            if (_album == null)
                return;

            var favourite = _nativeCore.ToggleFavourite(_album);
            FavouriteAlbumButton.Content = favourite
                ? "♥  Favourite"
                : "♡  Favourite";

            await LoadLibrary();
            Toast(favourite ? "Added to Favourites" : "Removed from Favourites");
        }

        async void SendToCoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (_album == null)
                return;

            if (!_core.HasSavedLogin)
            {
                UpdateSettings();
                ShowContent(SettingsPanel);
                TransferCoreUrlBox.Focus(FocusState.Programmatic);
                Toast("Connect the destination Core first");
                return;
            }

            SendToCoreButton.IsEnabled = false;
            try
            {
                Toast("Sending album to Core…");
                var result = await _core.ImportAlbumAsync(_album);
                var imported = JsonNumber(result, "imported", 0);
                var existing = JsonNumber(result, "already_present", 0);
                Toast("Core transfer complete · " +
                    imported.ToString("0") + " imported" +
                    (existing > 0 ? " · " + existing.ToString("0") + " already there" : ""));
            }
            catch (Exception ex)
            {
                Toast("Core transfer: " + ex.Message);
            }
            finally
            {
                SendToCoreButton.IsEnabled = true;
            }
        }

        async void TracksList_ItemClick(object sender, ItemClickEventArgs e)
        {
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
                await _nativeCore.PlayAlbumAsync(_album);
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
                _nativeCore.Control(action);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Toast(ex.Message);
            }
        }

        async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            var playing = PlaybackService.Instance.Snapshot().Playing;
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

        async void TransferCoreConnectButton_Click(object sender, RoutedEventArgs e)
        {
            TransferCoreStatusText.Text = "Connecting…";
            try
            {
                await _core.LoginAsync(
                    TransferCoreUrlBox.Text,
                    TransferCoreUsernameBox.Text,
                    TransferCorePasswordBox.Password);
                TransferCorePasswordBox.Password = "";
                TransferCoreUrlBox.Text = _core.BaseUrl ?? TransferCoreUrlBox.Text;
                TransferCoreStatusText.Text = "Ready · verified imports enabled";
                Toast("Destination Core connected");
            }
            catch (Exception ex)
            {
                TransferCoreStatusText.Text = ex.Message;
            }
        }

        void SettingsNavButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateSettings();
            ShowContent(SettingsPanel);
        }

        void RevealCorePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            Toast("Xbox Core login · admin / " + _nativeCore.AdminPassword);
        }

        void RotateCorePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            var password = _nativeCore.RotateAdminCredentials();
            Toast("New Xbox Core login · admin / " + password);
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
            try
            {
                var queue = _nativeCore.GetQueue();
                QueueList.ItemsSource = queue;
                QueueSummaryText.Text = queue.Count == 0
                    ? "Nothing queued"
                    : queue.Count +
                      (queue.Count == 1 ? " item" : " items") +
                      " · Xbox";
            }
            catch (Exception ex)
            {
                QueueSummaryText.Text = ex.Message;
            }

            await Task.CompletedTask;
        }

        async Task ObserveCompletedRipAsync()
        {
            var changed = false;
            var label = "New album added to Your Music";

            var status = await _nativeCore.GetRipStatusAsync();
            if (string.Equals(
                JsonString(status, "state", "idle"),
                "complete",
                StringComparison.OrdinalIgnoreCase))
            {
                var fingerprint = JsonString(status, "fingerprint", "");
                if (!string.IsNullOrWhiteSpace(fingerprint) &&
                    !string.Equals(
                        fingerprint,
                        _lastObservedRipFingerprint,
                        StringComparison.Ordinal))
                {
                    _lastObservedRipFingerprint = fingerprint;
                    changed = true;
                }
            }

            var bluRay = await _nativeCore.GetBluRayRipStatusAsync();
            if (string.Equals(
                JsonString(bluRay, "state", "idle"),
                "source_staged",
                StringComparison.OrdinalIgnoreCase))
            {
                var manifest = JsonString(bluRay, "manifest", "");
                if (!string.IsNullOrWhiteSpace(manifest) &&
                    !string.Equals(
                        manifest,
                        _lastObservedBluRayManifest,
                        StringComparison.Ordinal))
                {
                    _lastObservedBluRayManifest = manifest;
                    changed = true;
                    label = "Blu-ray title added to Your Music";
                }
            }

            if (!changed)
                return;

            await LoadLibrary();
            Toast(label);
        }

        async Task RefreshRipStatus()
        {
            try
            {
                SendBluRayToCoreButton.Visibility = Visibility.Collapsed;
                var bluRayRip = await _nativeCore.GetBluRayRipStatusAsync();
                var bluRayState = JsonString(bluRayRip, "state", "idle");
                if (bluRayState == "ripping")
                {
                    var progress = JsonNumber(bluRayRip, "progress_percent", 0);
                    var current = JsonString(bluRayRip, "current_file", "");
                    RipModeText.Text = "BLU-RAY AUTO-RIP";
                    RipStatusText.Text = "Ripping Blu-ray · " +
                        progress.ToString("0.0") + "%";
                    RipDetailText.Text = string.IsNullOrWhiteSpace(current)
                        ? "Copying the selected lossless title into Xbox Core."
                        : "Reading " + current + " into local Core staging.";
                    BluRayOptionsPanel.Visibility = Visibility.Collapsed;
                    return;
                }

                if (bluRayState == "source_staged")
                {
                    RipModeText.Text = "BLU-RAY AUTO-RIP";
                    RipStatusText.Text = "Blu-ray source rip complete";
                    RipDetailText.Text =
                        "The selected MPLS title and source clips are safely staged on Xbox. Finish on another Core to reconstruct the exact playlist and publish verified stereo/multichannel FLAC.";
                    BluRayOptionsPanel.Visibility = Visibility.Collapsed;
                    SendBluRayToCoreButton.Visibility = Visibility.Visible;
                    return;
                }

                if (bluRayState == "protected")
                {
                    RipModeText.Text = "BLU-RAY AUDIO";
                    RipStatusText.Text = "Protected Blu-ray detected";
                    RipDetailText.Text =
                        "Xbox has identified the disc, playlists and chapters, but this title is AACS-protected and the Xbox UWP sandbox does not expose decrypted media for ripping.";
                    BluRayOptionsPanel.Visibility = Visibility.Visible;
                }

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

                var bluRay = await _nativeCore.ScanBluRayAsync();
                if (JsonBool(bluRay, "available") &&
                    JsonNumber(bluRay, "title_count", 0) > 0)
                {
                    var options = new List<BluRayTitleOption>();
                    if (bluRay.ContainsKey("titles") &&
                        bluRay["titles"].ValueType == Windows.Data.Json.JsonValueType.Array)
                    {
                        foreach (var value in bluRay.GetNamedArray("titles"))
                        {
                            if (value.ValueType != Windows.Data.Json.JsonValueType.Object)
                                continue;

                            var title = value.GetObject();
                            var seconds = JsonNumber(title, "duration_seconds", 0);
                            var recommended = JsonBool(title, "recommended");
                            var playlist = JsonString(title, "playlist", "");
                            var playlistId = JsonString(title, "playlist_id", playlist);

                            options.Add(new BluRayTitleOption
                            {
                                Playlist = playlist,
                                Label = (recommended ? "Main title · " : "") +
                                    playlistId + " · " + FormatDuration(seconds)
                            });
                        }
                    }

                    BluRayTitleCombo.ItemsSource = options;
                    if (options.Count > 0)
                        BluRayTitleCombo.SelectedIndex = 0;

                    BluRayOptionsPanel.Visibility = Visibility.Visible;
                    RipModeText.Text = "BLU-RAY AUDIO";
                    RipStatusText.Text = JsonString(bluRay, "disc_label", "Blu-ray") +
                        " · " + options.Count +
                        (options.Count == 1 ? " title" : " titles");
                    RipDetailText.Text = JsonBool(bluRay, "protection_detected")
                        ? "Playlist structure found. This disc is AACS-protected, so Xbox can inspect titles and chapters but cannot produce a decrypted rip from the UWP sandbox."
                        : "Choose a title. Audio is preserved bit-for-bit into MKV; video stays off unless you tick Keep video.";
                    return;
                }

                BluRayOptionsPanel.Visibility = Visibility.Collapsed;

                var probe = await _nativeCore.ProbeOpticalAsync();
                var probeState = JsonString(probe, "status", "unknown");
                var devices = JsonNumber(probe, "device_count", 0);

                RipStatusText.Text = devices > 0
                    ? "Waiting for a music disc"
                    : "Optical drive unavailable";
                RipDetailText.Text = devices > 0
                    ? "Xbox optical ingest is armed. Insert CD, DVD-A or Blu-ray Audio."
                    : "Optical probe: " + probeState;
            }
            catch (Exception ex)
            {
                RipStatusText.Text = "Rip status unavailable";
                RipDetailText.Text = ex.Message;
            }
        }

        async void SendBluRayToCoreButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!_core.HasSavedLogin)
            {
                UpdateSettings();
                ShowContent(SettingsPanel);
                TransferCoreUrlBox.Focus(FocusState.Programmatic);
                Toast("Connect the destination Core first");
                return;
            }

            SendBluRayToCoreButton.IsEnabled = false;
            try
            {
                var status = await _nativeCore.GetBluRayRipStatusAsync();
                RipStatusText.Text = "Sending Blu-ray title to Core…";
                RipDetailText.Text =
                    "Uploading the exact MPLS clip chain. Core Ingest will reconstruct the selected title and publish the best stereo plus each multichannel layout.";

                var result = await _core.ImportStagedBluRayAsync(status);
                var ingestResult =
                    result.ContainsKey("result") &&
                    result["result"].ValueType == Windows.Data.Json.JsonValueType.Object
                        ? result.GetNamedObject("result")
                        : null;
                var outputs = ingestResult != null &&
                              ingestResult.ContainsKey("outputs") &&
                              ingestResult["outputs"].ValueType == Windows.Data.Json.JsonValueType.Array
                    ? ingestResult.GetNamedArray("outputs").Count
                    : 0;

                RipStatusText.Text = "Blu-ray published on Core";
                RipDetailText.Text =
                    "Playlist reconstructed and verified" +
                    (outputs > 0
                        ? " · " + outputs +
                          (outputs == 1 ? " audio edition" : " audio editions")
                        : "") +
                    ". The Xbox source rip is retained as recovery media.";
                Toast("Blu-ray Core ingest complete");
            }
            catch (Exception ex)
            {
                RipStatusText.Text = "Blu-ray Core ingest failed";
                RipDetailText.Text = ex.Message;
                Toast(ex.Message);
            }
            finally
            {
                SendBluRayToCoreButton.IsEnabled = true;
            }
        }

        async void PrepareBluRayButton_Click(object sender, RoutedEventArgs e)
        {
            var option = BluRayTitleCombo.SelectedItem as BluRayTitleOption;
            if (option == null)
            {
                Toast("Choose a Blu-ray title first");
                return;
            }

            PrepareBluRayButton.IsEnabled = false;
            try
            {
                var result = await _nativeCore.RipBluRayTitleAsync(
                    option.Playlist,
                    KeepBluRayVideoCheckBox.IsChecked == true);

                var state = JsonString(result, "state", "");
                RipStatusText.Text = state == "protected"
                    ? "Protected Blu-ray detected"
                    : "Blu-ray rip started";
                RipDetailText.Text = state == "protected"
                    ? "Xbox can read the Blu-ray structure but AACS prevents a decrypted media rip inside the Xbox sandbox."
                    : "The selected title is being copied losslessly into local Core staging.";
                Toast(state == "protected"
                    ? "Protected Blu-ray detected"
                    : "Blu-ray rip started");
            }
            catch (Exception ex)
            {
                Toast(ex.Message);
            }
            finally
            {
                PrepareBluRayButton.IsEnabled = true;
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
            var p = PlaybackService.Instance.Snapshot();
            var title = string.IsNullOrWhiteSpace(p.Title)
                ? "Nothing playing"
                : p.Title;
            var artist = p.Artist ?? "";
            var album = p.Album ?? "";
            var state = p.State ?? "";
            var playing = p.Playing;

            NowTitleText.Text = title;
            NowArtistText.Text = artist;
            NowAlbumText.Text = album;
            FooterTitleText.Text = title;
            FooterMetaText.Text = string.Join(
                " · ",
                new[] { artist, album, state }
                    .Where(v => !string.IsNullOrWhiteSpace(v)));
            PlayPauseButton.Content = playing
                ? "Ⅱ  Pause"
                : "▶  Play";
            FooterPlayPauseButton.Content = playing
                ? "Ⅱ"
                : "▶";

            OutputText.Text =
                PlaybackService.Instance.AudioOutputName;
            FooterOutputText.Text =
                PlaybackService.Instance.AudioOutputName;

            if (MusicPanel.Visibility == Visibility.Visible)
                RefreshHomeHero();
        }

        void UpdateSettings()
        {
            SettingsEditionText.Text = "Standalone Core";
            TransferCoreUrlBox.Text =
                _core.BaseUrl ??
                TransferCoreUrlBox.Text ??
                "";
            TransferCoreStatusText.Text =
                _core.HasSavedLogin
                    ? "Ready · " + (_core.BaseUrl ?? "")
                    : "Optional · not connected";

            SettingsCoreText.Text = _nativeCore.Running
                ? "Xbox Core · " +
                  (_nativeCore.ApiAddress ??
                   "http://xbox-core.local:8096")
                : "Starting local Core…";
            SettingsEndpointText.Text =
                XboxIdentity.EndpointId;
        }

        void ShowFirstRun()
        {
            ShowShell();
        }

        void ShowSetup()
        {
            FirstRunPanel.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
            ShellPanel.Visibility = Visibility.Collapsed;
            ConfigureEdition();

            CoreUrlBox.Text = _core.BaseUrl ?? "";
            CoreUrlBox.Focus(FocusState.Programmatic);
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

            ConnectionText.Text = _nativeCore.Ready
                ? "Standalone Xbox Core ready"
                : "Standalone Xbox Core starting";
            OutputText.Text =
                PlaybackService.Instance.AudioOutputName;
            FooterOutputText.Text =
                PlaybackService.Instance.AudioOutputName;
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

        static string FormatDuration(double seconds)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return span.TotalHours >= 1
                ? ((int)span.TotalHours).ToString("0") + ":" +
                  span.Minutes.ToString("00") + ":" +
                  span.Seconds.ToString("00")
                : span.Minutes.ToString("0") + ":" +
                  span.Seconds.ToString("00");
        }

        sealed class BluRayTitleOption
        {
            public string Playlist { get; set; }
            public string Label { get; set; }
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
