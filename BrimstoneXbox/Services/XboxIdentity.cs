using System;
using System.Linq;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Security.Cryptography;
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
                    {
                        return "coreaudio:xbox-" + hex.Substring(0, Math.Min(24, hex.Length));
                    }
                }
                catch
                {
                }

                return "coreaudio:xbox-" + Environment.MachineName.ToLowerInvariant();
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
