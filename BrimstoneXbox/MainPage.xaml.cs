using BrimstoneXbox.Models;
using BrimstoneXbox.Services;
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BrimstoneXbox
{
    public sealed partial class MainPage : Page
    {
        private readonly CoreClient _core = new CoreClient();
        private readonly DispatcherTimer _heartbeat = new DispatcherTimer();
        private readonly DispatcherTimer _refresh = new DispatcherTimer();
        private EndpointServer _server;
        private CoreAlbum _album;

        public MainPage()
        {
            InitializeComponent();
            Loaded += MainPage_Loaded;

            PlaybackService.Instance.StateChanged += (s, e) =>
                Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Low,
                    UpdatePlaybackUi);

            _heartbeat.Interval = TimeSpan.FromSeconds(25);
            _heartbeat.Tick += Heartbeat_Tick;

            _refresh.Interval = TimeSpan.FromSeconds(1);
            _refresh.Tick += (s, e) => UpdatePlaybackUi();
            _refresh.Start();
        }
    }
}
