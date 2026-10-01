using BrimstoneXbox.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding;

namespace BrimstoneXbox.Services
{
    public sealed class EndpointServer : IDisposable
    {
        public const string Port = "8095";

        private readonly Func<string> _tokenProvider;
        private readonly PlaybackService _playback;
        private StreamSocketListener _listener;
        private JsonObject _prepared;

        public EndpointServer(Func<string> tokenProvider)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _playback = PlaybackService.Instance;
        }

        public string Address
        {
            get
            {
                var ip = XboxIdentity.LocalAddress;
                return string.IsNullOrWhiteSpace(ip) ? null : "http://" + ip + ":" + Port;
            }
        }

        public async Task StartAsync()
        {
            if (_listener != null)
                return;

            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnectionReceived;
            await _listener.BindServiceNameAsync(Port);
        }

        public void Dispose()
        {
            if (_listener != null)
            {
                _listener.ConnectionReceived -= OnConnectionReceived;
                _listener.Dispose();
                _listener = null;
            }
        }

        private async void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            using (var socket = args.Socket)
            {
                try
                {
                    var request = await ReadRequestAsync(socket);
                    var response = Handle(request);
                    await WriteResponseAsync(socket, response.Status, response.Body);
                }
                catch (Exception ex)
                {
                    await WriteResponseAsync(socket, 500, Error(ex.Message).Stringify());
                }
            }
        }

        private EndpointResponse Handle(HttpRequest request)
        {
            if (request.Path == "/v1/capabilities" && request.Method == "GET")
                return Ok(Capabilities());

            if (!Authorised(request))
                return new EndpointResponse(401, Error("unauthorised").Stringify());

            if (request.Path == "/v1/status" && request.Method == "GET")
                return Ok(StatusJson());

            if (request.Method != "POST")
                return new EndpointResponse(404, Error("not found").Stringify());

            JsonObject data;
            try
            {
                data = string.IsNullOrWhiteSpace(request.Body)
                    ? new JsonObject()
                    : JsonObject.Parse(request.Body);
            }
            catch
            {
                return new EndpointResponse(400, Error("invalid json").Stringify());
            }

            switch (request.Path)
            {
                case "/v1/prepare":
                    if (string.IsNullOrWhiteSpace(JsonString(data, "url")))
                        return new EndpointResponse(400, Error("url required").Stringify());
                    _prepared = data;
                    return Ok(new JsonObject
                    {
                        ["ok"] = JsonValue.CreateBooleanValue(true)
                    });

                case "/v1/start":
                    if (_prepared == null || string.IsNullOrWhiteSpace(JsonString(_prepared, "url")))
                        return new EndpointResponse(409, Error("nothing prepared").Stringify());
                    Play(_prepared);
                    return Ok(StatusJson());

                case "/v1/play":
                    if (string.IsNullOrWhiteSpace(JsonString(data, "url")))
                        return new EndpointResponse(400, Error("url required").Stringify());
                    Play(data);
                    return Ok(StatusJson());

                case "/v1/programme":
                    return PlayProgramme(data);

                case "/v1/pause":
                    _playback.Pause();
                    return Ok(StatusJson());

                case "/v1/resume":
                    _playback.Resume();
                    return Ok(StatusJson());

                case "/v1/seek":
                    _playback.Seek(JsonNumber(data, "position_seconds", 0));
                    return Ok(StatusJson());

                case "/v1/stop":
                    _playback.Stop();
                    return Ok(new JsonObject
                    {
                        ["ok"] = JsonValue.CreateBooleanValue(true)
                    });

                default:
                    return new EndpointResponse(404, Error("not found").Stringify());
            }
        }

        private void Play(JsonObject data)
        {
            var source = data.ContainsKey("source") && data["source"].ValueType == JsonValueType.Object
                ? data.GetNamedObject("source")
                : null;

            _playback.PlayUrl(
                JsonString(data, "url"),
                source,
                JsonNumber(data, "volume", 0.70),
                JsonNumber(data, "position_seconds", 0));
        }

        private EndpointResponse PlayProgramme(JsonObject data)
        {
            if (!data.ContainsKey("urls") || data["urls"].ValueType != JsonValueType.Array)
                return new EndpointResponse(400, Error("urls required").Stringify());

            var urls = new List<string>();
            foreach (var value in data.GetNamedArray("urls"))
            {
                if (value.ValueType == JsonValueType.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    urls.Add(value.GetString());
            }

            if (urls.Count == 0)
                return new EndpointResponse(400, Error("urls required").Stringify());

            JsonArray sources = null;
            if (data.ContainsKey("sources") && data["sources"].ValueType == JsonValueType.Array)
                sources = data.GetNamedArray("sources");

            _playback.PlayProgramme(
                urls,
                sources,
                JsonNumber(data, "volume", 0.70),
                JsonNumber(data, "position_seconds", 0));

            return Ok(StatusJson());
        }

        private bool Authorised(HttpRequest request)
        {
            string supplied;
            if (!request.Headers.TryGetValue("authorization", out supplied))
                return false;

            var expected = _tokenProvider() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(expected))
                return false;

            const string prefix = "Bearer ";
            if (!supplied.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            return SlowEquals(expected, supplied.Substring(prefix.Length).Trim());
        }

        private static bool SlowEquals(string expected, string supplied)
        {
            var a = Encoding.UTF8.GetBytes(expected ?? string.Empty);
            var b = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
            var difference = a.Length ^ b.Length;
            var length = Math.Max(a.Length, b.Length);

            for (var i = 0; i < length; i++)
            {
                var av = i < a.Length ? a[i] : (byte)0;
                var bv = i < b.Length ? b[i] : (byte)0;
                difference |= av ^ bv;
            }

            return difference == 0;
        }

        private JsonObject Capabilities()
        {
            var controls = new JsonArray();
            controls.Add(JsonValue.CreateStringValue("play"));
            controls.Add(JsonValue.CreateStringValue("pause"));
            controls.Add(JsonValue.CreateStringValue("resume"));
            controls.Add(JsonValue.CreateStringValue("stop"));
            controls.Add(JsonValue.CreateStringValue("seek"));

            var transports = new JsonArray();
            transports.Add(JsonValue.CreateStringValue("pcm"));

            return new JsonObject
            {
                ["kind"] = JsonValue.CreateStringValue("coreaudio"),
                ["hostname"] = JsonValue.CreateStringValue(XboxIdentity.FriendlyName),
                ["endpoint_scope"] = JsonValue.CreateStringValue("core-end"),
                ["control_api"] = JsonValue.CreateStringValue("surround-agent-v2"),
                ["uri_playback"] = JsonValue.CreateBooleanValue(true),
                ["programme_playback"] = JsonValue.CreateBooleanValue(true),
                ["background_audio"] = JsonValue.CreateBooleanValue(true),
                ["direct_pcm"] = JsonValue.CreateBooleanValue(false),
                ["controls"] = controls,
                ["transports"] = transports
            };
        }

        private JsonObject StatusJson()
        {
            var s = _playback.Snapshot();
            return new JsonObject
            {
                ["playing"] = JsonValue.CreateBooleanValue(s.Playing),
                ["state"] = JsonValue.CreateStringValue(s.State ?? "idle"),
                ["title"] = JsonValue.CreateStringValue(s.Title ?? string.Empty),
                ["artist"] = JsonValue.CreateStringValue(s.Artist ?? string.Empty),
                ["album"] = JsonValue.CreateStringValue(s.Album ?? string.Empty),
                ["position_seconds"] = JsonValue.CreateNumberValue(s.PositionSeconds),
                ["duration_seconds"] = JsonValue.CreateNumberValue(s.DurationSeconds),
                ["volume"] = JsonValue.CreateNumberValue(s.Volume),
                ["muted"] = JsonValue.CreateBooleanValue(s.Muted)
            };
        }

        private static JsonObject Error(string message)
        {
            return new JsonObject
            {
                ["error"] = JsonValue.CreateStringValue(message ?? "error")
            };
        }

        private static EndpointResponse Ok(JsonObject body)
        {
            return new EndpointResponse(200, body.Stringify());
        }

        private static async Task<HttpRequest> ReadRequestAsync(StreamSocket socket)
        {
            using (var reader = new DataReader(socket.InputStream))
            {
                reader.UnicodeEncoding = UnicodeEncoding.Utf8;
                reader.InputStreamOptions = InputStreamOptions.Partial;

                var text = new StringBuilder();
                var contentLength = 0;
                var headerEnd = -1;

                while (true)
                {
                    var loaded = await reader.LoadAsync(2048);
                    if (loaded == 0)
                        break;

                    text.Append(reader.ReadString(loaded));

                    if (headerEnd < 0)
                    {
                        headerEnd = text.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (headerEnd >= 0)
                        {
                            var headerText = text.ToString().Substring(0, headerEnd);
                            contentLength = ParseContentLength(headerText);
                        }
                    }

                    if (headerEnd >= 0 && text.Length >= headerEnd + 4 + contentLength)
                        break;

                    if (text.Length > 1024 * 1024)
                        throw new InvalidOperationException("Request too large.");
                }

                return ParseRequest(text.ToString());
            }
        }

        private static int ParseContentLength(string headerText)
        {
            foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;

                if (!line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;

                int value;
                return int.TryParse(line.Substring(colon + 1).Trim(), out value) ? Math.Max(0, value) : 0;
            }

            return 0;
        }

        private static HttpRequest ParseRequest(string raw)
        {
            var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var headerText = split >= 0 ? raw.Substring(0, split) : raw;
            var body = split >= 0 ? raw.Substring(split + 4) : string.Empty;
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);

            if (lines.Length == 0)
                throw new InvalidOperationException("Empty request.");

            var first = lines[0].Split(' ');
            if (first.Length < 2)
                throw new InvalidOperationException("Invalid request line.");

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0)
                    continue;
                headers[lines[i].Substring(0, colon).Trim().ToLowerInvariant()] =
                    lines[i].Substring(colon + 1).Trim();
            }

            return new HttpRequest
            {
                Method = first[0].ToUpperInvariant(),
                Path = first[1],
                Headers = headers,
                Body = body
            };
        }

        private static async Task WriteResponseAsync(StreamSocket socket, int status, string body)
        {
            body = body ?? "{}";
            var bytes = Encoding.UTF8.GetBytes(body);
            var reason = status == 200 ? "OK" :
                         status == 400 ? "Bad Request" :
                         status == 401 ? "Unauthorized" :
                         status == 404 ? "Not Found" :
                         status == 409 ? "Conflict" : "Server Error";

            var header =
                "HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + bytes.Length + "\r\n" +
                "Connection: close\r\n\r\n";

            using (var writer = new DataWriter(socket.OutputStream))
            {
                writer.UnicodeEncoding = UnicodeEncoding.Utf8;
                writer.WriteString(header);
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }
        }

        private static string JsonString(JsonObject obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key].ValueType != JsonValueType.String)
                return string.Empty;
            return obj[key].GetString();
        }

        private static double JsonNumber(JsonObject obj, string key, double fallback)
        {
            if (obj == null || !obj.ContainsKey(key))
                return fallback;

            if (obj[key].ValueType == JsonValueType.Number)
                return obj[key].GetNumber();

            return fallback;
        }

        private sealed class HttpRequest
        {
            public string Method { get; set; }
            public string Path { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public string Body { get; set; }
        }

        private sealed class EndpointResponse
        {
            public EndpointResponse(int status, string body)
            {
                Status = status;
                Body = body;
            }

            public int Status { get; }
            public string Body { get; }
        }
    }
}
