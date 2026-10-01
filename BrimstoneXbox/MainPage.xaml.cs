using BrimstoneXbox.Models;
using BrimstoneXbox.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace BrimstoneXbox
{
    public sealed partial class MainPage : Page
    {
        readonly CoreClient _core = new CoreClient();
        readonly DispatcherTimer _heartbeat = new DispatcherTimer();
        readonly DispatcherTimer _refresh = new DispatcherTimer();
        EndpointServer _server;
        CoreAlbum _album;

        public MainPage()
        {
            InitializeComponent();
            Loaded += MainPage_Loaded;
            PlaybackService.Instance.StateChanged += (s,e) =>
                Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, UpdatePlaybackUi);
            _heartbeat.Interval = TimeSpan.FromSeconds(25);
            _heartbeat.Tick += Heartbeat_Tick;
            _refresh.Interval = TimeSpan.FromSeconds(1);
            _refresh.Tick += (s,e) => UpdatePlaybackUi();
            _refresh.Start();
        }

        async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            CoreUrlBox.Text = string.IsNullOrWhiteSpace(_core.BaseUrl) ? "http://10.26.30.20:8080" : _core.BaseUrl;
            if (!_core.HasSavedLogin) { Show(LoginPanel); return; }
            try { await BringOnline(true); await LoadLibrary(); Show(MusicPanel); }
            catch (Exception ex) { LoginStatusText.Text = ex.Message; Show(LoginPanel); }
        }

        async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            ConnectButton.IsEnabled = false;
            try
            {
                LoginStatusText.Text = "Signing in...";
                await _core.LoginAsync(CoreUrlBox.Text, UsernameBox.Text, PasswordBox.Password);
                LoginStatusText.Text = "Pairing Xbox...";
                await BringOnline(true);
                await LoadLibrary();
                PasswordBox.Password = "";
                LoginStatusText.Text = "";
                Show(MusicPanel);
                Toast("Xbox paired to Brimstone Core");
            }
            catch (Exception ex) { LoginStatusText.Text = ex.Message; Toast(ex.Message); }
            finally { ConnectButton.IsEnabled = true; }
        }

        async Task BringOnline(bool allowPair)
        {
            if (_server == null) { _server = new EndpointServer(() => _core.DeviceToken); await _server.StartAsync(); }
            if (string.IsNullOrWhiteSpace(_server.Address)) throw new InvalidOperationException("Xbox has no usable LAN address.");
            if (!_core.HasDeviceCredential)
            {
                if (!allowPair) throw new InvalidOperationException("Xbox is not paired.");
                await _core.PairDeviceAsync(XboxIdentity.EndpointId);
            }
            await _core.RegisterEndpointAsync(XboxIdentity.EndpointId, XboxIdentity.FriendlyName, _server.Address);
            ConnectionText.Text = "Core connected - Xbox online";
            UpdateSettings();
            _heartbeat.Start();
        }

        async void Heartbeat_Tick(object sender, object e)
        {
            if (_server == null || !_core.HasDeviceCredential) return;
            try
            {
                await _core.RegisterEndpointAsync(XboxIdentity.EndpointId, XboxIdentity.FriendlyName, _server.Address);
                ConnectionText.Text = "Core connected - Xbox online";
            }
            catch { ConnectionText.Text = "Core connection interrupted"; }
        }

        async Task LoadLibrary()
        {
            var albums = await _core.GetAlbumsAsync();
            AlbumGrid.ItemsSource = albums;
            LibrarySummaryText.Text = albums.Count + (albums.Count == 1 ? " album" : " albums") + " from Core";
        }

        void AlbumGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            _album = e.ClickedItem as CoreAlbum;
            if (_album == null) return;
            AlbumTitleText.Text = _album.Title;
            AlbumArtistText.Text = _album.Artist;
            TracksList.ItemsSource = _album.Tracks;
            Show(AlbumPanel);
        }

        async void TracksList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var track = e.ClickedItem as CoreTrack;
            if (track == null) return;
            try { await _core.PlayTrackAsync(XboxIdentity.EndpointId, track.Id); Show(NowPlayingPanel); }
            catch (Exception ex) { Toast(ex.Message); }
        }

        async void PlayAlbumButton_Click(object sender, RoutedEventArgs e)
        {
            if (_album == null) return;
            try { await _core.PlayAlbumAsync(XboxIdentity.EndpointId, _album.Tracks.Select(t => t.Id)); Show(NowPlayingPanel); }
            catch (Exception ex) { Toast(ex.Message); }
        }

        async Task Transport(string action)
        {
            try { await _core.ControlAsync(XboxIdentity.EndpointId, action); }
            catch (Exception ex) { Toast(ex.Message); }
        }

        async void PlayPauseButton_Click(object sender, RoutedEventArgs e) =>
            await Transport(PlaybackService.Instance.Snapshot().Playing ? "pause" : "resume");
        async void StopButton_Click(object sender, RoutedEventArgs e) => await Transport("stop");
        async void PreviousButton_Click(object sender, RoutedEventArgs e) => await Transport("previous");
        async void NextButton_Click(object sender, RoutedEventArgs e) => await Transport("next");

        void MusicNavButton_Click(object sender, RoutedEventArgs e) => Show(MusicPanel);
        void NowNavButton_Click(object sender, RoutedEventArgs e) => Show(NowPlayingPanel);
        void RipNavButton_Click(object sender, RoutedEventArgs e) => Show(RipPanel);
        void BackToLibraryButton_Click(object sender, RoutedEventArgs e) => Show(MusicPanel);

        void SettingsNavButton_Click(object sender, RoutedEventArgs e) { UpdateSettings(); Show(SettingsPanel); }

        async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try { await LoadLibrary(); Toast("Library refreshed"); }
            catch (Exception ex) { Toast(ex.Message); }
        }

        async void RepairButton_Click(object sender, RoutedEventArgs e)
        {
            try { await _core.PairDeviceAsync(XboxIdentity.EndpointId); await BringOnline(false); Toast("Xbox re-paired"); }
            catch (Exception ex) { Toast(ex.Message); }
        }

        void ForgetButton_Click(object sender, RoutedEventArgs e)
        {
            _heartbeat.Stop(); _core.Forget(); _server?.Dispose(); _server = null;
            AlbumGrid.ItemsSource = null; ConnectionText.Text = "Not connected"; Show(LoginPanel);
        }

        void Page_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.GamepadView) { Show(NowPlayingPanel); e.Handled = true; }
            else if (e.Key == VirtualKey.GamepadMenu) { UpdateSettings(); Show(SettingsPanel); e.Handled = true; }
            else if (e.Key == VirtualKey.GamepadB && AlbumPanel.Visibility == Visibility.Visible) { Show(MusicPanel); e.Handled = true; }
        }

        void UpdatePlaybackUi()
        {
            var p = PlaybackService.Instance.Snapshot();
            var title = string.IsNullOrWhiteSpace(p.Title) ? "Nothing playing" : p.Title;
            NowTitleText.Text = title; NowArtistText.Text = p.Artist ?? ""; NowAlbumText.Text = p.Album ?? "";
            FooterTitleText.Text = title;
            FooterMetaText.Text = string.Join(" · ", new[] { p.Artist, p.Album, p.State }.Where(v => !string.IsNullOrWhiteSpace(v)));
            PlayPauseButton.Content = p.Playing ? "Pause" : "Play";
            FooterPlayPauseButton.Content = p.Playing ? "Ⅱ" : "▶";
        }

        void UpdateSettings()
        {
            SettingsCoreText.Text = string.IsNullOrWhiteSpace(_core.BaseUrl) ? "Not configured" : _core.BaseUrl;
            SettingsEndpointText.Text = XboxIdentity.EndpointId;
            SettingsAddressText.Text = _server?.Address ?? "Listener not started";
        }

        void Show(UIElement panel)
        {
            LoginPanel.Visibility = MusicPanel.Visibility = AlbumPanel.Visibility =
            NowPlayingPanel.Visibility = RipPanel.Visibility = SettingsPanel.Visibility = Visibility.Collapsed;
            panel.Visibility = Visibility.Visible;
        }

        void Toast(string message) { ToastText.Text = message ?? ""; ToastBorder.Visibility = Visibility.Visible; }
    }
}
