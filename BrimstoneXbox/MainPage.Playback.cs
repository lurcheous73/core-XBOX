using BrimstoneXbox.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;

namespace BrimstoneXbox
{
    public sealed partial class MainPage
    {
        private async Task TransportAsync(string action)
        {
            try
            {
                await _core.ControlAsync(XboxIdentity.EndpointId, action);
            }
            catch (Exception ex)
            {
                Toast(ex.Message);
            }
        }

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            await TransportAsync(
                PlaybackService.Instance.Snapshot().Playing ? "pause" : "resume");
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            await TransportAsync("stop");
        }

        private async void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            await TransportAsync("previous");
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            await TransportAsync("next");
        }

        private void UpdatePlaybackUi()
        {
            var p = PlaybackService.Instance.Snapshot();
            var title = string.IsNullOrWhiteSpace(p.Title)
                ? "Nothing playing"
                : p.Title;

            NowTitleText.Text = title;
            NowArtistText.Text = p.Artist ?? string.Empty;
            NowAlbumText.Text = p.Album ?? string.Empty;
            FooterTitleText.Text = title;

            FooterMetaText.Text = string.Join(
                " · ",
                new[] { p.Artist, p.Album, p.State }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            PlayPauseButton.Content = p.Playing ? "Pause" : "Play";
            FooterPlayPauseButton.Content = p.Playing ? "Ⅱ" : "▶";
        }
    }
}
