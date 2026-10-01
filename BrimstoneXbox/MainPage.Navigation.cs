using BrimstoneXbox.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace BrimstoneXbox
{
    public sealed partial class MainPage
    {
        private void MusicNavButton_Click(object sender, RoutedEventArgs e)
        {
            Show(MusicPanel);
        }

        private void NowNavButton_Click(object sender, RoutedEventArgs e)
        {
            Show(NowPlayingPanel);
        }

        private void RipNavButton_Click(object sender, RoutedEventArgs e)
        {
            Show(RipPanel);
        }

        private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateSettings();
            Show(SettingsPanel);
        }

        private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.GamepadView)
            {
                Show(NowPlayingPanel);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.GamepadMenu)
            {
                UpdateSettings();
                Show(SettingsPanel);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.GamepadB &&
                     AlbumPanel.Visibility == Visibility.Visible)
            {
                Show(MusicPanel);
                e.Handled = true;
            }
        }

        private void UpdateSettings()
        {
            SettingsCoreText.Text = string.IsNullOrWhiteSpace(_core.BaseUrl)
                ? "Not configured"
                : _core.BaseUrl;

            SettingsEndpointText.Text = XboxIdentity.EndpointId;
            SettingsAddressText.Text = _server?.Address ?? "Listener not started";
        }

        private void Show(UIElement panel)
        {
            LoginPanel.Visibility = Visibility.Collapsed;
            MusicPanel.Visibility = Visibility.Collapsed;
            AlbumPanel.Visibility = Visibility.Collapsed;
            NowPlayingPanel.Visibility = Visibility.Collapsed;
            RipPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            panel.Visibility = Visibility.Visible;
        }

        private void Toast(string message)
        {
            ToastText.Text = message ?? string.Empty;
            ToastBorder.Visibility = Visibility.Visible;
        }
    }
}
