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
    public sealed class LocalCoreApiServer : IDisposable
    {
        public const string Port = "8096";

        readonly Func<JsonObject> _healthProvider;
        readonly Func<JsonObject> _runtimeProvider;
        readonly Func<JsonObject> _opticalProvider;
        readonly Func<JsonObject> _stackProvider;
        StreamSocketListener _listener;

        public LocalCoreApiServer(
            Func<JsonObject> healthProvider,
            Func<JsonObject> runtimeProvider,
            Func<JsonObject> opticalProvider,
            Func<JsonObject> stackProvider)
        {
            _healthProvider = healthProvider ?? throw new ArgumentNullException(nameof(healthProvider));
            _runtimeProvider = runtimeProvider ?? throw new ArgumentNullException(nameof(runtimeProvider));
            _opticalProvider = opticalProvider ?? throw new ArgumentNullException(nameof(opticalProvider));
            _stackProvider = stackProvider ?? throw new ArgumentNullException(nameof(stackProvider));
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
            if (_listener != null) return;

            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnectionReceived;
            await _listener.BindServiceNameAsync(Port);
        }

        async void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            using (var socket = args.Socket)
            {
                try
                {
                    var request = await ReadRequestAsync(socket);
                    if (request.Method != "GET")
                    {
                        await WriteResponseAsync(socket, 405, Error("method not allowed").Stringify());
                        return;
                    }

                    JsonObject body;
                    switch (request.Path)
                    {
                        case "/api/v1/health":
                            body = _healthProvider();
                            break;
                        case "/api/v1/runtime":
                            body = _runtimeProvider();
                            break;
                        case "/api/v1/optical/probe":
                            body = _opticalProvider();
                            break;
                        case "/api/v1/stack":
                        case "/api/v1/system/stack":
                            body = _stackProvider();
                            break;
                        default:
                            await WriteResponseAsync(socket, 404, Error("not found").Stringify());
                            return;
                    }

                    await WriteResponseAsync(socket, 200, body.Stringify());
                }
                catch (Exception ex)
                {
                    try { await WriteResponseAsync(socket, 500, Error(ex.Message).Stringify()); }
                    catch { }
                }
            }
        }

        static async Task<Request> ReadRequestAsync(StreamSocket socket)
        {
            using (var reader = new DataReader(socket.InputStream))
            {
                reader.UnicodeEncoding = UnicodeEncoding.Utf8;
                reader.InputStreamOptions = InputStreamOptions.Partial;
                var loaded = await reader.LoadAsync(4096);
                if (loaded == 0) throw new InvalidOperationException("Empty request.");

                var raw = reader.ReadString(loaded);
                var lineEnd = raw.IndexOf("\r\n", StringComparison.Ordinal);
                var first = (lineEnd >= 0 ? raw.Substring(0, lineEnd) : raw).Split(' ');
                if (first.Length < 2) throw new InvalidOperationException("Invalid request.");

                return new Request
                {
                    Method = first[0].ToUpperInvariant(),
                    Path = first[1].Split('?')[0]
                };
            }
        }

        static JsonObject Error(string message)
        {
            return new JsonObject
            {
                ["error"] = JsonValue.CreateStringValue(message ?? "error")
            };
        }

        static async Task WriteResponseAsync(StreamSocket socket, int status, string body)
        {
            body = body ?? "{}";
            var bytes = Encoding.UTF8.GetBytes(body);
            var reason = status == 200 ? "OK" :
                         status == 404 ? "Not Found" :
                         status == 405 ? "Method Not Allowed" : "Server Error";

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
        }
    }
}
