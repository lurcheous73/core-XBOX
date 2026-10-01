using BrimstoneXbox.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace BrimstoneXbox.Services
{
    public sealed class CoreClient
    {
        private readonly HttpClient _http = new HttpClient();
        private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;

        public string BaseUrl { get; private set; }
        public string UserToken { get; private set; }
        public string DeviceToken { get; private set; }

        public CoreClient()
        {
            _http.Timeout = TimeSpan.FromSeconds(30);
            BaseUrl = NormaliseBaseUrl(ReadSetting("coreUrl"));
            UserToken = ReadSetting("userToken");
            DeviceToken = ReadSetting("deviceToken");
        }

        public bool HasSavedLogin => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(UserToken);
        public bool HasDeviceCredential => !string.IsNullOrWhiteSpace(DeviceToken);

        public async Task LoginAsync(string coreUrl, string username, string password)
        {
            BaseUrl = NormaliseBaseUrl(coreUrl);
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
            WriteSetting("coreUrl", BaseUrl);
            WriteSetting("userToken", UserToken);
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

            WriteSetting("deviceToken", DeviceToken);
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
                ["software_version"] = JsonValue.CreateStringValue("0.1.0"),
                ["capabilities"] = capabilities
            };

            await SendAsync(HttpMethod.Post, "/api/v1/endpoints/register", payload, DeviceToken);
        }

        public async Task<List<CoreAlbum>> GetAlbumsAsync()
        {
            RequireUserToken();
            var json = await SendAsync(HttpMethod.Get, "/api/v1/catalog/albums", null, UserToken);
            var root = JsonObject.Parse(json);
            var albums = new List<CoreAlbum>();

            if (!root.ContainsKey("albums") || root["albums"].ValueType != JsonValueType.Array)
                return albums;

            foreach (var value in root.GetNamedArray("albums"))
            {
                if (value.ValueType != JsonValueType.Object)
                    continue;

                var source = value.GetObject();
                var album = new CoreAlbum
                {
                    Id = IdValue(source, "id"),
                    Title = StringValue(source, "title", "Untitled"),
                    Artist = StringValue(source, "artist", "Unknown artist")
                };

                if (source.ContainsKey("editions") && source["editions"].ValueType == JsonValueType.Array)
                {
                    var editions = source.GetNamedArray("editions");
                    if (editions.Count > 0 && editions[0].ValueType == JsonValueType.Object)
                    {
                        var edition = editions[0].GetObject();
                        if (edition.ContainsKey("tracks") && edition["tracks"].ValueType == JsonValueType.Array)
                        {
                            foreach (var trackValue in edition.GetNamedArray("tracks"))
                            {
                                if (trackValue.ValueType != JsonValueType.Object)
                                    continue;

                                var trackJson = trackValue.GetObject();
                                var metadata = trackJson.ContainsKey("metadata") &&
                                               trackJson["metadata"].ValueType == JsonValueType.Object
                                    ? trackJson.GetNamedObject("metadata")
                                    : null;

                                long trackId;
                                if (!long.TryParse(IdValue(trackJson, "id"), out trackId))
                                    continue;

                                album.Tracks.Add(new CoreTrack
                                {
                                    Id = trackId,
                                    Title = metadata != null
                                        ? StringValue(metadata, "title", StringValue(trackJson, "title", "Track"))
                                        : StringValue(trackJson, "title", "Track"),
                                    Artist = metadata != null
                                        ? StringValue(metadata, "artist", album.Artist)
                                        : StringValue(trackJson, "artist", album.Artist),
                                    Album = album.Title,
                                    DurationSeconds = NumberValue(trackJson, "duration_seconds",
                                        metadata == null ? 0 : NumberValue(metadata, "duration_seconds", 0))
                                });
                            }
                        }
                    }
                }

                albums.Add(album);
            }

            return albums
                .OrderBy(a => a.Artist ?? string.Empty)
                .ThenBy(a => a.Title ?? string.Empty)
                .ToList();
        }

        public async Task PlayTrackAsync(string endpointId, long mediaId)
        {
            RequireUserToken();
            var payload = new JsonObject
            {
                ["media_id"] = JsonValue.CreateNumberValue(mediaId)
            };

            await SendAsync(
                HttpMethod.Post,
                "/api/v1/endpoints/" + Uri.EscapeDataString(endpointId) + "/play",
                payload,
                UserToken);
        }

        public async Task PlayAlbumAsync(string endpointId, IEnumerable<long> mediaIds)
        {
            RequireUserToken();

            var ids = new JsonArray();
            foreach (var id in mediaIds)
                ids.Add(JsonValue.CreateNumberValue(id));

            if (ids.Count == 0)
                throw new InvalidOperationException("This album has no playable tracks.");

            var payload = new JsonObject
            {
                ["media_ids"] = ids
            };

            await SendAsync(
                HttpMethod.Post,
                "/api/v1/endpoints/" + Uri.EscapeDataString(endpointId) + "/programme",
                payload,
                UserToken);
        }

        public async Task ControlAsync(string endpointId, string action)
        {
            RequireUserToken();
            await SendAsync(
                HttpMethod.Post,
                "/api/v1/playback/" + Uri.EscapeDataString(endpointId) + "/control/" + Uri.EscapeDataString(action),
                null,
                UserToken);
        }

        public void Forget()
        {
            BaseUrl = string.Empty;
            UserToken = string.Empty;
            DeviceToken = string.Empty;
            _settings.Values.Remove("coreUrl");
            _settings.Values.Remove("userToken");
            _settings.Values.Remove("deviceToken");
        }

        private async Task<string> SendAsync(HttpMethod method, string path, JsonObject payload, string bearer)
        {
            var request = new HttpRequestMessage(method, new Uri(BaseUrl.TrimEnd('/') + path));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("Brimstone-Xbox/0.1.0");

            if (!string.IsNullOrWhiteSpace(bearer))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            if (payload != null)
                request.Content = new StringContent(payload.Stringify(), Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
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
            var value = (raw ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                value = "http://" + value;

            return value;
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
