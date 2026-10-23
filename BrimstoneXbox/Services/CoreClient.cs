using BrimstoneXbox.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BrimstoneXbox.Services
{
    public sealed partial class CoreClient
    {
        private readonly HttpClient _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;

        public string BaseUrl { get; private set; }
        public string UserToken { get; private set; }
        public string DeviceToken { get; private set; }

        public CoreClient()
        {
            _http.Timeout = TimeSpan.FromSeconds(30);
            BaseUrl = NormaliseBaseUrl(ReadSetting("coreUrl"));
            UserToken = LoadCredential("user");
            DeviceToken = LoadCredential("device");
            MigrateCredential("userToken", "user");
            MigrateCredential("deviceToken", "device");
            UserToken = LoadCredential("user");
            DeviceToken = LoadCredential("device");
        }

        public bool HasSavedLogin => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(UserToken);
        public bool HasDeviceCredential => !string.IsNullOrWhiteSpace(DeviceToken);

        public async Task LoginAsync(string coreUrl, string username, string password)
        {
            var nextUrl = NormaliseBaseUrl(coreUrl);
            if (!string.Equals(BaseUrl, nextUrl, StringComparison.OrdinalIgnoreCase)) { UserToken = ""; DeviceToken = ""; }
            BaseUrl = nextUrl;
            if (string.IsNullOrWhiteSpace(BaseUrl))
                throw new InvalidOperationException("Enter the Brimstone Core address.");

            var payload = new JsonObject
            {
                ["username"] = JsonValue.CreateStringValue(string.IsNullOrWhiteSpace(username) ? "admin" : username.Trim()),
                ["password"] = JsonValue.CreateStringValue(password ?? string.Empty)
            };

            var response = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", payload, null);
            var parsed = JsonObject.Parse(response);
            var token = StringValue(parsed, "token");

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Core did not return an access token.");

            UserToken = token;
            DeviceToken = "";
            SaveCredential("device", "");
            WriteSetting("coreUrl", BaseUrl);
            SaveCredential("user", UserToken);
        }

        public async Task<string> PairDeviceAsync(string deviceId)
        {
            RequireUserToken();

            var pairing = await SendAsync(
                HttpMethod.Post,
                "/api/v1/device-trust/pairing-code?role=core-end",
                null,
                UserToken);

            var pairingObject = JsonObject.Parse(pairing);
            var code = StringValue(pairingObject, "code");
            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("Core did not return a pairing code.");

            var payload = new JsonObject
            {
                ["code"] = JsonValue.CreateStringValue(code),
                ["device_id"] = JsonValue.CreateStringValue(deviceId),
                ["role"] = JsonValue.CreateStringValue("core-end")
            };

            var paired = await SendAsync(
                HttpMethod.Post,
                "/api/v1/device-trust/pair",
                payload,
                null);

            var pairedObject = JsonObject.Parse(paired);
            DeviceToken = StringValue(pairedObject, "token");
            if (string.IsNullOrWhiteSpace(DeviceToken))
                throw new InvalidOperationException("Core did not return the Xbox device credential.");

            SaveCredential("device", DeviceToken);
            return DeviceToken;
        }

        public async Task RegisterEndpointAsync(string endpointId, string friendlyName, string address)
        {
            if (string.IsNullOrWhiteSpace(DeviceToken))
                throw new InvalidOperationException("Xbox is not paired to Core.");

            var capabilities = new JsonObject
            {
                ["control_api"] = JsonValue.CreateStringValue("surround-agent-v2"),
                ["uri_playback"] = JsonValue.CreateBooleanValue(true),
                ["programme_playback"] = JsonValue.CreateBooleanValue(true),
                ["queue_editing"] = JsonValue.CreateBooleanValue(false),
                ["scheduled_group_playback"] = JsonValue.CreateBooleanValue(false),
                ["direct_pcm"] = JsonValue.CreateBooleanValue(false),
                ["background_audio"] = JsonValue.CreateBooleanValue(true),
                ["platform"] = JsonValue.CreateStringValue("xbox-uwp")
            };

            var transports = new JsonArray();
            transports.Add(JsonValue.CreateStringValue("pcm"));
            capabilities["transports"] = transports;

            var controls = new JsonArray();
            controls.Add(JsonValue.CreateStringValue("play"));
            controls.Add(JsonValue.CreateStringValue("pause"));
            controls.Add(JsonValue.CreateStringValue("resume"));
            controls.Add(JsonValue.CreateStringValue("stop"));
            controls.Add(JsonValue.CreateStringValue("seek"));
            capabilities["controls"] = controls;

            var payload = new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue(endpointId),
                ["name"] = JsonValue.CreateStringValue(friendlyName),
                ["kind"] = JsonValue.CreateStringValue("coreaudio"),
                ["address"] = JsonValue.CreateStringValue(address),
                ["protocol_version"] = JsonValue.CreateNumberValue(1),
                ["software_version"] = JsonValue.CreateStringValue("2.5.0"),
                ["capabilities"] = capabilities
            };

            await SendAsync(HttpMethod.Post, "/api/v1/endpoints/register", payload, DeviceToken);
        }

        public void Forget()
        {
            SaveCredential("user", "");
            SaveCredential("device", "");
            BaseUrl = string.Empty;
            UserToken = string.Empty;
            DeviceToken = string.Empty;
            _settings.Values.Remove("coreUrl");
            _settings.Values.Remove("userToken");
            _settings.Values.Remove("deviceToken");
        }

        private async Task<string> SendAsync(HttpMethod method, string path, JsonObject payload, string bearer)
        {
            using (var request = new HttpRequestMessage(method, new Uri(BaseUrl.TrimEnd('/') + path)))
            {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("Brimstone-Xbox-Control/2.5.0");

            if (!string.IsNullOrWhiteSpace(bearer))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            if (payload != null)
                request.Content = new StringContent(payload.Stringify(), Encoding.UTF8, "application/json");

            using (var response = await _http.SendAsync(request))
            {
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var message = "Core request failed (" + (int)response.StatusCode + ").";
                try
                {
                    var error = JsonObject.Parse(body);
                    message = StringValue(error, "detail",
                        StringValue(error, "message", StringValue(error, "error", message)));
                }
                catch
                {
                }
                throw new InvalidOperationException(message);
            }

            return string.IsNullOrWhiteSpace(body) ? "{}" : body;
            }
            }
        }

        private void RequireUserToken()
        {
            if (string.IsNullOrWhiteSpace(UserToken))
                throw new InvalidOperationException("Sign in to Core first.");
        }

        private string ReadSetting(string key)
        {
            object value;
            return _settings.Values.TryGetValue(key, out value) ? value as string ?? string.Empty : string.Empty;
        }

        private void WriteSetting(string key, string value)
        {
            _settings.Values[key] = value ?? string.Empty;
        }

        private static string NormaliseBaseUrl(string raw)
        {
            var value = (raw ?? "").Trim().TrimEnd('/');
            if (value.Length == 0) return "";
            if (!value.Contains("://")) value = "https://" + value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidOperationException("Enter a valid Core HTTP or HTTPS address.");
            if (uri.Scheme == "http" && !IsLocalHost(uri.Host))
                throw new InvalidOperationException("Remote Core connections require HTTPS.");
            return value;
        }

        static bool IsLocalHost(string host)
        {
            if (host == "localhost" || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
            System.Net.IPAddress ip;
            if (!System.Net.IPAddress.TryParse(host, out ip)) return false;
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
        }

        string CredentialResource => "BrimstoneXboxControl:" + BaseUrl;

        string LoadCredential(string role)
        {
            if (string.IsNullOrWhiteSpace(BaseUrl)) return "";
            try { var c = new Windows.Security.Credentials.PasswordVault().Retrieve(CredentialResource, role);
                c.RetrievePassword(); return c.Password; } catch { return ""; }
        }

        void SaveCredential(string role, string value)
        {
            if (string.IsNullOrWhiteSpace(BaseUrl)) return;
            var vault = new Windows.Security.Credentials.PasswordVault();
            try { vault.Remove(vault.Retrieve(CredentialResource, role)); } catch { }
            if (!string.IsNullOrWhiteSpace(value)) vault.Add(new Windows.Security.Credentials.PasswordCredential(CredentialResource, role, value));
        }

        void MigrateCredential(string key, string role)
        {
            var old = ReadSetting(key);
            if (string.IsNullOrWhiteSpace(old)) return;
            if (string.IsNullOrWhiteSpace(LoadCredential(role))) SaveCredential(role, old);
            _settings.Values.Remove(key);
        }

        public async Task<JsonObject> RequestAsync(string path, string method = "GET", JsonObject payload = null)
        {
            RequireUserToken();
            if (!path.StartsWith("/api/v1/", StringComparison.Ordinal) || path.Contains(".."))
                throw new InvalidOperationException("Invalid Core API route.");
            return JsonObject.Parse(await SendAsync(new HttpMethod(method), path, payload, UserToken));
        }

        private static string StringValue(JsonObject obj, string key, string fallback = "")
        {
            if (obj == null || !obj.ContainsKey(key))
                return fallback;

            var value = obj[key];
            if (value.ValueType == JsonValueType.String)
                return value.GetString();

            if (value.ValueType == JsonValueType.Number)
                return value.GetNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);

            return fallback;
        }

        private static string IdValue(JsonObject obj, string key)
        {
            return StringValue(obj, key, string.Empty);
        }

        private static double NumberValue(JsonObject obj, string key, double fallback)
        {
            if (obj == null || !obj.ContainsKey(key))
                return fallback;

            var value = obj[key];
            if (value.ValueType == JsonValueType.Number)
                return value.GetNumber();

            if (value.ValueType == JsonValueType.String)
            {
                double parsed;
                if (double.TryParse(value.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out parsed))
                    return parsed;
            }

            return fallback;
        }
    }
}
