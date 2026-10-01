using BrimstoneXbox.Models;
using BrimstoneXbox.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BrimstoneXbox
{
    public sealed partial class MainPage
    {
        private async Task LoadLibraryAsync()
        {
            var albums = await _core.GetAlbumsAsync();
            AlbumGrid.ItemsSource = albums;
            LibrarySummaryText.Text =
                albums.Count + (albums.Count == 1 ? " album" : " albums") + " from Core";
        }

        private void AlbumGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            _album = e.ClickedItem as CoreAlbum;
            if (_album == null)
                return;

            AlbumTitleText.Text = _album.Title;
            AlbumArtistText.Text = _album.Artist;
            TracksList.ItemsSource = _album.Tracks;
            Show(AlbumPanel);
            PlayAlbumButton.Focus(FocusState.Programmatic);
        }

        private async void TracksList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var track = e.ClickedItem as CoreTrack;
            if (track == null)
                return;

            try
            {
                await _core.PlayTrackAsync(XboxIdentity.EndpointId, track.Id);
                Show(NowPlayingPanel);
                Toast("Playing " + track.Title);
            }
            catch (Exception ex)
            {
                Toast(ex.Message);
            }
        }

        private async void PlayAlbumButton_Click(object sender, RoutedEventArgs e)
        {
            if (_album == null)
                return;

            try
            {
                await _core.PlayAlbumAsync(
                    XboxIdentity.EndpointId,
                    _album.Tracks.Select(t => t.Id));

                Show(NowPlayingPanel);
                Toast("Playing " + _album.Title);
            }
            catch (Exception ex)
            {
                Toast(ex.Message);
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadLibraryAsync();
                Toast("Library refreshed");
            }
            catch (Exception ex)
            {
                Toast(ex.Message);
            }
        }

        private void BackToLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            Show(MusicPanel);
            AlbumGrid.Focus(FocusState.Programmatic);
        }
    }
}
