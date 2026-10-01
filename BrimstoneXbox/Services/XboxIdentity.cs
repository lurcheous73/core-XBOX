using System;
using System.Linq;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.System.Profile;

namespace BrimstoneXbox.Services
{
    public static class XboxIdentity
    {
        public static string EndpointId
        {
            get
            {
                try
                {
                    var info = SystemIdentification.GetSystemIdForPublisher();
                    var hex = CryptographicBuffer.EncodeToHexString(info.Id).ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(hex))
                        return "coreaudio:xbox-" + hex.Substring(0, Math.Min(24, hex.Length));
                }
                catch
                {
                }

                var settings = ApplicationData.Current.LocalSettings;
                object saved;
                if (settings.Values.TryGetValue("fallbackEndpointId", out saved))
                {
                    var value = saved as string;
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }

                var created = "coreaudio:xbox-" + Guid.NewGuid().ToString("N");
                settings.Values["fallbackEndpointId"] = created;
                return created;
            }
        }

        public static string FriendlyName => "Brimstone Xbox One X";

        public static string LocalAddress
        {
            get
            {
                var host = NetworkInformation.GetHostNames()
                    .FirstOrDefault(h =>
                        h.Type == HostNameType.Ipv4 &&
                        h.IPInformation != null &&
                        h.IPInformation.NetworkAdapter != null);

                return host?.CanonicalName;
            }
        }
    }
}
