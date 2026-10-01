using BrimstoneXbox.Services;
using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;

namespace BrimstoneXbox
{
    public sealed partial class MainPage
    {
        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            CoreUrlBox.Text = string.IsNullOrWhiteSpace(_core.BaseUrl)
                ? "http://10.26.30.20:8080"
                : _core.BaseUrl;

            if (!_core.HasSavedLogin)
            {
                Show(LoginPanel);
                ConnectionText.Text = "Not connected";
                return;
            }

            try
            {
                LoginStatusText.Text = "Restoring Core connection...";
                await BringOnlineAsync(true);
                await LoadLibraryAsync();
                Show(MusicPanel);
            }
            catch (Exception ex)
            {
                LoginStatusText.Text = ex.Message;
                ConnectionText.Text = "Reconnect required";
                Show(LoginPanel);
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            ConnectButton.IsEnabled = false;
            try
            {
                LoginStatusText.Text = "Signing in...";
                await _core.LoginAsync(
                    CoreUrlBox.Text,
                    UsernameBox.Text,
                    PasswordBox.Password);

                LoginStatusText.Text = "Pairing Xbox...";
                await BringOnlineAsync(true);
                await LoadLibraryAsync();

                PasswordBox.Password = string.Empty;
                LoginStatusText.Text = string.Empty;
                Show(MusicPanel);
                Toast("Xbox paired to Brimstone Core");
            }
            catch (Exception ex)
            {
                LoginStatusText.Text = ex.Message;
                Toast(ex.Message);
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }

        private async Task BringOnlineAsync(bool allowPair)
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
                if (!allowPair)
                    throw new InvalidOperationException("Xbox is not paired.");

                await _core.PairDeviceAsync(XboxIdentity.EndpointId);
            }

            await _core.RegisterEndpointAsync(
                XboxIdentity.EndpointId,
                XboxIdentity.FriendlyName,
                _server.Address);

            ConnectionText.Text = "Core connected - Xbox online";
            UpdateSettings();
            _heartbeat.Start();
        }

        private async void Heartbeat_Tick(object sender, object e)
        {
            if (_server == null || !_core.HasDeviceCredential)
                return;

            try
            {
                await _core.RegisterEndpointAsync(
                    XboxIdentity.EndpointId,
                    XboxIdentity.FriendlyName,
                    _server.Address);
                ConnectionText.Text = "Core connected - Xbox online";
            }
            catch
            {
                ConnectionText.Text = "Core connection interrupted";
            }
        }

        private async void RepairButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _core.PairDeviceAsync(XboxIdentity.EndpointId);
                await BringOnlineAsync(false);
                Toast("Xbox re-paired");
            }
            catch (Exception ex)
            {
                Toast(ex.Message);
            }
        }

        private void ForgetButton_Click(object sender, RoutedEventArgs e)
        {
            _heartbeat.Stop();
            _core.Forget();
            _server?.Dispose();
            _server = null;
            AlbumGrid.ItemsSource = null;
            ConnectionText.Text = "Not connected";
            LoginStatusText.Text = string.Empty;
            Show(LoginPanel);
        }
    }
}
