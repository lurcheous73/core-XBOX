using BrimstoneXbox.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace BrimstoneXbox.Services
{
    public sealed class SooloosClient
    {
        readonly HttpClient _http = new HttpClient();
        string _sessionId = "";
        int _requestId = 1;

        public string Host { get; private set; }

        public SooloosClient(string host)
        {
            Host = NormaliseHost(host);
            _http.Timeout = TimeSpan.FromSeconds(8);
        }

        public async Task<List<CoreAlbum>> SearchAlbumsAsync(string query = "")
        {
            var fields = new JsonObject
            {
                ["Substring"] = JsonValue.CreateStringValue(query ?? ""),
                ["MaxAlbumCount"] = JsonValue.CreateNumberValue(1500),
                ["MaxTrackCount"] = JsonValue.CreateNumberValue(0)
            };
            var rows = await RequestAsync("Sooloos.Msg.Music.FastSearchRequest", fields);
            var response = Message(rows, "FastSearchResponse");
            var result = new List<CoreAlbum>();
            if (response == null || !response.ContainsKey("Albums") || response["Albums"].ValueType != JsonValueType.Array)
                return result;

            foreach (var value in response.GetNamedArray("Albums"))
            {
                if (value.ValueType != JsonValueType.Object) continue;
                var raw = value.GetObject();
                var id = StringValue(raw, "AlbumId:sooid");
                if (string.IsNullOrWhiteSpace(id)) continue;
                result.Add(new CoreAlbum
                {
                    Id = id,
                    Artist = StringValue(raw, "ArtistName", "Unknown artist"),
                    Title = StringValue(raw, "AlbumName", "Untitled")
                });
            }
            return result.OrderBy(a => a.Artist ?? "").ThenBy(a => a.Title ?? "").ToList();
        }

        public async Task<List<SooloosZone>> ZonesAsync()
        {
            var rows = await RequestAsync("Sooloos.Msg.Zones.ZoneListSubscribeRequest", new JsonObject());
            var response = Message(rows, "ZoneListSubscriptionResponse");
            var result = new List<SooloosZone>();
            if (response == null || !response.ContainsKey("Zones") || response["Zones"].ValueType != JsonValueType.Object)
                return result;
            var zonesObject = response.GetNamedObject("Zones");
            if (!zonesObject.ContainsKey("Zones") || zonesObject["Zones"].ValueType != JsonValueType.Array)
                return result;

            foreach (var value in zonesObject.GetNamedArray("Zones"))
            {
                if (value.ValueType != JsonValueType.Object) continue;
                var zone = value.GetObject();
                var id = StringValue(zone, "ZoneId:sooid");
                if (string.IsNullOrWhiteSpace(id)) continue;

                var detailFields = new JsonObject
                {
                    ["ZoneId:sooid"] = JsonValue.CreateStringValue(id),
                    ["SendPlayQueueState"] = JsonValue.CreateBooleanValue(true)
                };
                var detailRows = await RequestAsync("Sooloos.Msg.Zones.ZoneSubscribeRequest", detailFields);
                var detail = Message(detailRows, "ZoneSubscribeResponse");
                var status = detail != null && detail.ContainsKey("Status") && detail["Status"].ValueType == JsonValueType.Object
                    ? detail.GetNamedObject("Status") : new JsonObject();
                var audio = status.ContainsKey("AudioDevice") && status["AudioDevice"].ValueType == JsonValueType.Object
                    ? status.GetNamedObject("AudioDevice") : new JsonObject();
                var media = status.ContainsKey("Media") && status["Media"].ValueType == JsonValueType.Object
                    ? status.GetNamedObject("Media") : new JsonObject();

                result.Add(new SooloosZone
                {
                    Id = id,
                    Name = StringValue(zone, "Name", "Meridian Zone"),
                    State = StringValue(status, "State", "Unknown"),
                    Volume = NumberValue(audio, "Volume", 0),
                    Muted = BoolValue(audio, "IsMuted"),
                    Title = StringValue(media, "Title"),
                    Subtitle = StringValue(media, "Subtitle")
                });
            }
            return result;
        }

        public async Task PlayAlbumAsync(string zoneId, string albumId)
        {
            var fields = new JsonObject
            {
                ["ZoneId:sooid"] = JsonValue.CreateStringValue(zoneId ?? ""),
                ["Priority"] = JsonValue.CreateStringValue("Now"),
                ["AlbumId:sooid"] = JsonValue.CreateStringValue(albumId ?? ""),
                ["PicksOnly"] = JsonValue.CreateBooleanValue(false)
            };
            await SendAsync("Sooloos.Msg.Music.PlayAlbumRequest", fields);
        }

        public async Task ControlAsync(string zoneId, string action)
        {
            string control;
            switch ((action ?? "").ToLowerInvariant())
            {
                case "pause": control = "Pause"; break;
                case "resume":
                case "play": control = "Play"; break;
                case "stop": control = "Stop"; break;
                case "next": control = "Next"; break;
                case "previous": control = "Previous"; break;
                default: throw new InvalidOperationException("Unsupported Sooloos transport action.");
            }
            var fields = new JsonObject
            {
                ["ZoneId:sooid"] = JsonValue.CreateStringValue(zoneId ?? ""),
                ["Control"] = JsonValue.CreateStringValue(control)
            };
            await SendAsync("Sooloos.Msg.Zones.ZoneControlRequest", fields);
        }

        async Task EnsureSessionAsync()
        {
            if (!string.IsNullOrWhiteSpace(_sessionId)) return;
            var response = await PostAsync("/message/session/newsession", null, "");
            IEnumerable<string> values;
            if (!response.Headers.TryGetValues("Message-Session", out values))
                response.Headers.TryGetValues("message-session", out values);
            _sessionId = values == null ? "" : values.FirstOrDefault() ?? "";
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(_sessionId))
                throw new InvalidOperationException("Sooloos did not create a message session.");
        }

        async Task<JsonArray> RequestAsync(string type, JsonObject fields)
        {
            await EnsureSessionAsync();
            var id = _requestId++;
            await SendRequestAsync(type, fields, id);
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var response = await PostAsync("/message/session/getresponses", null, _sessionId);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Sooloos response failed.");
                var body = await response.Content.ReadAsStringAsync();
                JsonArray rows;
                if (!JsonArray.TryParse(body, out rows)) rows = new JsonArray();
                foreach (var row in rows)
                {
                    if (row.ValueType != JsonValueType.Object) continue;
                    var obj = row.GetObject();
                    if (NumberValue(obj, "requestid", -1) == id) return rows;
                }
            }
            throw new InvalidOperationException("No Sooloos response was returned.");
        }

        async Task SendAsync(string type, JsonObject fields)
        {
            await EnsureSessionAsync();
            await SendRequestAsync(type, fields, 0);
        }

        async Task SendRequestAsync(string type, JsonObject fields, int requestId)
        {
            var row = new JsonObject
            {
                ["message:" + type] = fields ?? new JsonObject(),
                ["requestid"] = JsonValue.CreateNumberValue(requestId)
            };
            var body = new JsonArray();
            body.Add(row);
            var response = await PostAsync("/message/session/sendrequests", body.Stringify(), _sessionId);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Sooloos rejected the request.");
        }

        async Task<HttpResponseMessage> PostAsync(string path, string json, string session)
        {
            if (string.IsNullOrWhiteSpace(Host))
                throw new InvalidOperationException("Enter the Sooloos Core address.");
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri("http://" + Host + path));
            if (!string.IsNullOrWhiteSpace(session))
                request.Headers.TryAddWithoutValidation("Message-Session", session);
            if (json != null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return await _http.SendAsync(request);
        }

        static JsonObject Message(JsonArray rows, string suffix)
        {
            foreach (var row in rows)
            {
                if (row.ValueType != JsonValueType.Object) continue;
                var obj = row.GetObject();
                foreach (var pair in obj)
                    if (pair.Key.StartsWith("message:", StringComparison.Ordinal) &&
                        pair.Key.EndsWith(suffix, StringComparison.Ordinal) &&
                        pair.Value.ValueType == JsonValueType.Object)
                        return pair.Value.GetObject();
            }
            return null;
        }

        public static string NormaliseHost(string raw)
        {
            var value = (raw ?? "").Trim().TrimEnd('/');
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = value.Substring(7);
            if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = value.Substring(8);
            return value;
        }

        static string StringValue(JsonObject obj, string key, string fallback = "")
        {
            if (obj == null || !obj.ContainsKey(key)) return fallback;
            var value = obj[key];
            if (value.ValueType == JsonValueType.String) return value.GetString();
            if (value.ValueType == JsonValueType.Number)
                return value.GetNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
            return fallback;
        }

        static double NumberValue(JsonObject obj, string key, double fallback)
        {
            if (obj == null || !obj.ContainsKey(key)) return fallback;
            var value = obj[key];
            return value.ValueType == JsonValueType.Number ? value.GetNumber() : fallback;
        }

        static bool BoolValue(JsonObject obj, string key)
        {
            return obj != null && obj.ContainsKey(key) &&
                obj[key].ValueType == JsonValueType.Boolean && obj[key].GetBoolean();
        }
    }
}
