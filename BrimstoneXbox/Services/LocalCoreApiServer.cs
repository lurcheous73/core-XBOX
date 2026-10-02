using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.ServiceDiscovery.Dnssd;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding;

namespace BrimstoneXbox.Services
{
    public sealed class LocalCoreApiServer : IDisposable
    {
        public const string Port = "8096";

        readonly XboxCoreRuntime _runtime;
        StreamSocketListener _listener;
        DnssdServiceInstance _dnssd;
        string _discoveryStatus = "not_started";

        public LocalCoreApiServer(XboxCoreRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        public string DiscoveryStatus => _discoveryStatus;

        public string DiscoveryInstanceName =>
            _dnssd == null ? "" : _dnssd.DnssdServiceInstanceName ?? "";

        public string Address
        {
            get
            {
                var ip = XboxIdentity.LocalAddress;
                return string.IsNullOrWhiteSpace(ip)
                    ? null
                    : "http://" + ip + ":" + Port;
            }
        }

        public async Task StartAsync()
        {
            if (_listener != null) return;

            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnectionReceived;

            var address = XboxIdentity.LocalAddress;
            if (!string.IsNullOrWhiteSpace(address))
                await _listener.BindEndpointAsync(new HostName(address), Port);
            else
                await _listener.BindServiceNameAsync(Port);

            await RegisterDiscoveryAsync(address);
        }

        async Task RegisterDiscoveryAsync(string address)
        {
            try
            {
                HostName hostName = null;
                foreach (var host in NetworkInformation.GetHostNames())
                {
                    if (host.Type == HostNameType.DomainName &&
                        host.RawName != null &&
                        host.RawName.EndsWith(".local",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        hostName = host;
                        break;
                    }
                }

                if (hostName == null && !string.IsNullOrWhiteSpace(address))
                    hostName = new HostName(address);

                if (hostName == null)
                {
                    _discoveryStatus = "no_host";
                    return;
                }

                var endpointId = XboxIdentity.EndpointId ?? "xbox";
                var cleanId = endpointId
                    .Replace("coreaudio:", "")
                    .Replace(":", "-");
                var nodeId = "core-" + cleanId;
                var suffix = nodeId.Length > 6
                    ? nodeId.Substring(nodeId.Length - 6)
                    : nodeId;

                var instanceName =
                    "Xbox Core (" + suffix + ")._brimstone-core._tcp.local.";

                _dnssd = new DnssdServiceInstance(
                    instanceName,
                    hostName,
                    UInt16.Parse(Port));

                _dnssd.TextAttributes["product"] = "brimstone-core";
                _dnssd.TextAttributes["node_id"] = nodeId;
                _dnssd.TextAttributes["name"] = "Xbox Core";
                _dnssd.TextAttributes["role"] = "standalone";
                _dnssd.TextAttributes["system_id"] = "";
                _dnssd.TextAttributes["version"] = "0.2.6";
                _dnssd.TextAttributes["api_url"] = Address ?? "";

                var result =
                    await _dnssd.RegisterStreamSocketListenerAsync(_listener);
                _discoveryStatus = result.Status.ToString();
            }
            catch (Exception ex)
            {
                _discoveryStatus =
                    ex.GetType().Name + " 0x" +
                    ex.HResult.ToString("X8") + ": " + ex.Message;
            }
        }

        async void OnConnectionReceived(
            StreamSocketListener sender,
            StreamSocketListenerConnectionReceivedEventArgs args)
        {
            using (var socket = args.Socket)
            {
                try
                {
                    var request = await ReadRequestAsync(socket);
                    var response = await RouteAsync(request);
                    await WriteResponseAsync(
                        socket,
                        response.Status,
                        response.Body == null ? "{}" : response.Body.Stringify());
                }
                catch (UnauthorizedAccessException ex)
                {
                    await SafeWriteAsync(socket, 401, Error(ex.Message));
                }
                catch (ArgumentException ex)
                {
                    await SafeWriteAsync(socket, 400, Error(ex.Message));
                }
                catch (InvalidOperationException ex)
                {
                    await SafeWriteAsync(socket, 400, Error(ex.Message));
                }
                catch (Exception ex)
                {
                    await SafeWriteAsync(socket, 500,
                        Error(ex.GetType().Name + ": " + ex.Message));
                }
            }
        }

        async Task<ApiResponse> RouteAsync(Request request)
        {
            if (request.Method == "GET" && request.Path == "/api/v1/health")
                return Ok(_runtime.BuildHealth());

            if (request.Method == "GET" && request.Path == "/api/v1/runtime")
                return Ok(_runtime.BuildRuntime());

            if (request.Method == "GET" &&
                (request.Path == "/api/v1/stack" ||
                 request.Path == "/api/v1/system/stack"))
                return Ok(_runtime.BuildStack());

            if (request.Method == "GET" &&
                request.Path == "/api/v1/optical/probe")
                return Ok(_runtime.BuildOptical());

            if (request.Method == "GET" &&
                request.Path == "/api/v1/auth/state")
                return Ok(_runtime.BuildAuthState());

            if (request.Method == "POST" &&
                request.Path == "/api/v1/auth/login")
            {
                var body = ParseBody(request);
                return Ok(_runtime.Login(
                    JsonString(body, "username", "admin"),
                    JsonString(body, "password", "")));
            }

            RequireAuthorization(request);

            if (request.Method == "GET" &&
                request.Path == "/api/v1/auth/me")
                return Ok(_runtime.BuildAuthMe());

            if (request.Method == "GET" &&
                request.Path == "/api/v1/catalog/albums")
                return Ok(await _runtime.BuildCatalogApiAsync());

            if (request.Method == "GET" &&
                request.Path == "/api/v1/audio/output")
                return Ok(_runtime.BuildAudioOutput());

            if (request.Method == "GET" &&
                request.Path == "/api/v1/audio/outputs")
                return Ok(await _runtime.BuildAudioOutputsAsync());

            if (request.Method == "POST" &&
                request.Path == "/api/v1/audio/output")
            {
                var body = ParseBody(request);
                var id = JsonString(body, "id", "auto");
                return Ok(await _runtime.SetAudioOutputAsync(id));
            }

            if (request.Method == "GET" &&
                request.Path == "/api/v1/endpoints")
                return Ok(_runtime.BuildEndpoints());

            if (request.Method == "GET" &&
                request.Path == "/api/v1/zones")
                return Ok(_runtime.BuildZones());

            if (request.Method == "GET" &&
                IsEndpointStatusPath(request.Path))
                return Ok(_runtime.BuildEndpointStatus());

            if (request.Method == "POST" &&
                IsEndpointPlayPath(request.Path))
            {
                var body = ParseBody(request);
                var id = (long)JsonNumber(body, "media_id", -1);
                if (id < 0)
                    throw new ArgumentException("media_id is required.");

                await _runtime.PlayMediaIdAsync(id);
                return Ok(Success());
            }

            if (request.Method == "POST" &&
                IsEndpointProgrammePath(request.Path))
            {
                var body = ParseBody(request);
                if (!body.ContainsKey("media_ids") ||
                    body["media_ids"].ValueType != JsonValueType.Array)
                    throw new ArgumentException("media_ids is required.");

                var ids = new List<long>();
                foreach (var value in body.GetNamedArray("media_ids"))
                {
                    if (value.ValueType == JsonValueType.Number)
                        ids.Add((long)value.GetNumber());
                    else if (value.ValueType == JsonValueType.String)
                    {
                        long parsed;
                        if (long.TryParse(value.GetString(), out parsed))
                            ids.Add(parsed);
                    }
                }

                await _runtime.PlayProgrammeIdsAsync(ids);
                return Ok(Success());
            }

            if (request.Method == "GET" &&
                request.Path.StartsWith("/api/v1/playback/",
                    StringComparison.OrdinalIgnoreCase) &&
                request.Path.EndsWith("/status",
                    StringComparison.OrdinalIgnoreCase))
                return Ok(_runtime.BuildEndpointStatus());

            if (request.Method == "POST" &&
                request.Path.StartsWith("/api/v1/playback/",
                    StringComparison.OrdinalIgnoreCase) &&
                request.Path.EndsWith("/volume",
                    StringComparison.OrdinalIgnoreCase))
            {
                var volume = QueryNumber(request.Query, "volume", -1);
                if (volume < 0 || volume > 100)
                    throw new ArgumentException("volume must be between 0 and 100.");

                _runtime.SetVolume(volume);
                return Ok(new JsonObject
                {
                    ["ok"] = JsonValue.CreateBooleanValue(true),
                    ["volume"] = JsonValue.CreateNumberValue(volume)
                });
            }

            if (request.Method == "POST" &&
                request.Path.StartsWith("/api/v1/playback/",
                    StringComparison.OrdinalIgnoreCase) &&
                request.Path.EndsWith("/mute",
                    StringComparison.OrdinalIgnoreCase))
            {
                var muted = QueryBool(request.Query, "muted", false);
                _runtime.SetMuted(muted);
                return Ok(new JsonObject
                {
                    ["ok"] = JsonValue.CreateBooleanValue(true),
                    ["muted"] = JsonValue.CreateBooleanValue(muted)
                });
            }

            if (request.Method == "POST" &&
                request.Path.StartsWith("/api/v1/playback/",
                    StringComparison.OrdinalIgnoreCase) &&
                request.Path.EndsWith("/stop",
                    StringComparison.OrdinalIgnoreCase))
            {
                _runtime.Control("stop");
                return Ok(Success());
            }

            if (request.Method == "POST" &&
                request.Path.StartsWith("/api/v1/endpoints/",
                    StringComparison.OrdinalIgnoreCase) &&
                request.Path.EndsWith("/stop",
                    StringComparison.OrdinalIgnoreCase))
            {
                _runtime.Control("stop");
                return Ok(Success());
            }

            if (request.Method == "POST" &&
                request.Path.StartsWith("/api/v1/playback/",
                    StringComparison.OrdinalIgnoreCase) &&
                request.Path.IndexOf("/control/",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var marker = "/control/";
                var index = request.Path.IndexOf(
                    marker, StringComparison.OrdinalIgnoreCase);
                var action = index >= 0
                    ? request.Path.Substring(index + marker.Length)
                    : "";
                if (string.IsNullOrWhiteSpace(action))
                    throw new ArgumentException("Playback action is required.");

                _runtime.Control(Uri.UnescapeDataString(action));
                return Ok(Success());
            }

            if (request.Method == "GET" &&
                request.Path == "/api/v1/ingest/capabilities")
                return Ok(_runtime.BuildIngestCapabilities());

            if (request.Method == "GET" &&
                request.Path == "/api/v1/ingest/devices")
                return Ok(_runtime.BuildIngestDevices());

            if (request.Method == "GET" &&
                request.Path == "/api/v1/ingest/jobs")
                return Ok(await _runtime.BuildIngestJobsAsync());

            if (request.Method == "GET" &&
                request.Path == "/api/v1/ingest/health")
            {
                return Ok(new JsonObject
                {
                    ["ok"] = JsonValue.CreateBooleanValue(true),
                    ["service"] = JsonValue.CreateStringValue("surround-ingest"),
                    ["platform"] = JsonValue.CreateStringValue("xbox-customdevice")
                });
            }

            if (request.Method == "POST" &&
                request.Path == "/api/v1/ingest/rip")
            {
                var status = await _runtime.RipNowAsync();
                return Ok(status);
            }

            return new ApiResponse
            {
                Status = 404,
                Body = Error("not found")
            };
        }

        void RequireAuthorization(Request request)
        {
            string header;
            if (!request.Headers.TryGetValue("authorization", out header))
                throw new UnauthorizedAccessException("Bearer token required.");

            const string prefix = "Bearer ";
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Bearer token required.");

            var token = header.Substring(prefix.Length).Trim();
            if (!_runtime.IsAuthorized(token))
                throw new UnauthorizedAccessException("Invalid Core token.");
        }

        static bool IsEndpointStatusPath(string path)
        {
            return path.StartsWith("/api/v1/endpoints/",
                       StringComparison.OrdinalIgnoreCase) &&
                   path.EndsWith("/status",
                       StringComparison.OrdinalIgnoreCase);
        }

        static bool IsEndpointPlayPath(string path)
        {
            return path.StartsWith("/api/v1/endpoints/",
                       StringComparison.OrdinalIgnoreCase) &&
                   path.EndsWith("/play",
                       StringComparison.OrdinalIgnoreCase);
        }

        static bool IsEndpointProgrammePath(string path)
        {
            return path.StartsWith("/api/v1/endpoints/",
                       StringComparison.OrdinalIgnoreCase) &&
                   path.EndsWith("/programme",
                       StringComparison.OrdinalIgnoreCase);
        }

        static string QueryValue(string query, string key)
        {
            if (string.IsNullOrWhiteSpace(query))
                return "";

            foreach (var pair in query.Split('&'))
            {
                var equals = pair.IndexOf('=');
                var name = equals >= 0 ? pair.Substring(0, equals) : pair;
                var value = equals >= 0 ? pair.Substring(equals + 1) : "";
                if (string.Equals(
                    Uri.UnescapeDataString(name.Replace("+", " ")),
                    key,
                    StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(value.Replace("+", " "));
            }

            return "";
        }

        static double QueryNumber(string query, string key, double fallback)
        {
            double value;
            return double.TryParse(
                QueryValue(query, key),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out value)
                ? value
                : fallback;
        }

        static bool QueryBool(string query, string key, bool fallback)
        {
            var value = QueryValue(query, key);
            bool parsed;
            if (bool.TryParse(value, out parsed))
                return parsed;
            if (value == "1") return true;
            if (value == "0") return false;
            return fallback;
        }

        static JsonObject ParseBody(Request request)
        {
            if (string.IsNullOrWhiteSpace(request.Body))
                return new JsonObject();

            JsonObject body;
            if (!JsonObject.TryParse(request.Body, out body))
                throw new ArgumentException("Request body must be a JSON object.");
            return body;
        }

        static async Task<Request> ReadRequestAsync(StreamSocket socket)
        {
            using (var reader = new DataReader(socket.InputStream))
            {
                reader.UnicodeEncoding = UnicodeEncoding.Utf8;
                reader.InputStreamOptions = InputStreamOptions.Partial;

                var builder = new StringBuilder();
                int headerEnd = -1;
                int contentLength = 0;

                for (var i = 0; i < 64; i++)
                {
                    var loaded = await reader.LoadAsync(4096);
                    if (loaded == 0)
                        break;

                    builder.Append(reader.ReadString(loaded));
                    var raw = builder.ToString();

                    if (headerEnd < 0)
                    {
                        headerEnd = raw.IndexOf("\r\n\r\n",
                            StringComparison.Ordinal);
                        if (headerEnd >= 0)
                        {
                            var headerText = raw.Substring(0, headerEnd);
                            contentLength = ParseContentLength(headerText);
                        }
                    }

                    if (headerEnd >= 0)
                    {
                        var body = raw.Substring(headerEnd + 4);
                        if (Encoding.UTF8.GetByteCount(body) >= contentLength)
                            break;
                    }

                    if (builder.Length > 1024 * 1024)
                        throw new ArgumentException("HTTP request is too large.");
                }

                var text = builder.ToString();
                headerEnd = text.IndexOf("\r\n\r\n",
                    StringComparison.Ordinal);
                var headerBlock = headerEnd >= 0
                    ? text.Substring(0, headerEnd)
                    : text;
                var bodyText = headerEnd >= 0
                    ? text.Substring(headerEnd + 4)
                    : "";

                var lines = headerBlock.Split(
                    new[] { "\r\n" },
                    StringSplitOptions.None);
                if (lines.Length == 0)
                    throw new ArgumentException("Invalid HTTP request.");

                var first = lines[0].Split(' ');
                if (first.Length < 2)
                    throw new ArgumentException("Invalid HTTP request line.");

                var headers = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < lines.Length; i++)
                {
                    var colon = lines[i].IndexOf(':');
                    if (colon <= 0) continue;
                    var name = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                    var value = lines[i].Substring(colon + 1).Trim();
                    headers[name] = value;
                }

                var target = first[1];
                var query = "";
                var question = target.IndexOf('?');
                if (question >= 0)
                {
                    query = target.Substring(question + 1);
                    target = target.Substring(0, question);
                }

                return new Request
                {
                    Method = first[0].ToUpperInvariant(),
                    Path = Uri.UnescapeDataString(target),
                    Query = query,
                    Headers = headers,
                    Body = bodyText
                };
            }
        }

        static int ParseContentLength(string headers)
        {
            foreach (var line in headers.Split(
                new[] { "\r\n" },
                StringSplitOptions.None))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;

                var name = line.Substring(0, colon).Trim();
                if (!name.Equals("Content-Length",
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                int length;
                if (int.TryParse(
                    line.Substring(colon + 1).Trim(),
                    out length))
                    return Math.Max(0, length);
            }

            return 0;
        }

        static JsonObject Success()
        {
            return new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(true)
            };
        }

        static JsonObject Error(string message)
        {
            return new JsonObject
            {
                ["detail"] = JsonValue.CreateStringValue(message ?? "error"),
                ["error"] = JsonValue.CreateStringValue(message ?? "error")
            };
        }

        static string JsonString(
            JsonObject obj,
            string key,
            string fallback = "")
        {
            if (obj == null ||
                !obj.ContainsKey(key) ||
                obj[key].ValueType != JsonValueType.String)
                return fallback;

            return obj[key].GetString();
        }

        static double JsonNumber(
            JsonObject obj,
            string key,
            double fallback)
        {
            if (obj == null ||
                !obj.ContainsKey(key))
                return fallback;

            var value = obj[key];
            if (value.ValueType == JsonValueType.Number)
                return value.GetNumber();

            if (value.ValueType == JsonValueType.String)
            {
                double parsed;
                if (double.TryParse(
                    value.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out parsed))
                    return parsed;
            }

            return fallback;
        }

        static ApiResponse Ok(JsonObject body)
        {
            return new ApiResponse
            {
                Status = 200,
                Body = body ?? new JsonObject()
            };
        }

        static async Task SafeWriteAsync(
            StreamSocket socket,
            int status,
            JsonObject body)
        {
            try
            {
                await WriteResponseAsync(
                    socket,
                    status,
                    (body ?? new JsonObject()).Stringify());
            }
            catch { }
        }

        static async Task WriteResponseAsync(
            StreamSocket socket,
            int status,
            string body)
        {
            body = body ?? "{}";
            var bytes = Encoding.UTF8.GetBytes(body);
            var reason =
                status == 200 ? "OK" :
                status == 400 ? "Bad Request" :
                status == 401 ? "Unauthorized" :
                status == 404 ? "Not Found" :
                status == 405 ? "Method Not Allowed" :
                "Server Error";

            var header =
                "HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + bytes.Length + "\r\n" +
                "Cache-Control: no-store\r\n" +
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

        public void Dispose()
        {
            if (_listener != null)
            {
                _listener.ConnectionReceived -= OnConnectionReceived;
                _listener.Dispose();
                _listener = null;
            }
        }

        sealed class Request
        {
            public string Method { get; set; }
            public string Path { get; set; }
            public string Query { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public string Body { get; set; }
        }

        sealed class ApiResponse
        {
            public int Status { get; set; }
            public JsonObject Body { get; set; }
        }
    }
}
