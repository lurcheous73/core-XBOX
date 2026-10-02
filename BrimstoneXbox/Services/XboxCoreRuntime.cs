using System;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace BrimstoneXbox.Services
{
    public sealed class XboxCoreRuntime : IDisposable
    {
        readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
        LocalCoreApiServer _api;
        readonly OpticalProbeService _optical = new OpticalProbeService();
        readonly CdRipService _cdRip = new CdRipService();
        DateTimeOffset _startedAt;
        string _apiError = "";

        public bool Running { get; private set; }
        public string Edition { get; private set; } = "core";
        public string ApiAddress => _api?.Address;

        public Task<JsonObject> ProbeOpticalAsync() => _optical.ProbeAsync();

        public async Task StartAsync(string edition = "core")
        {
            if (Running) return;

            Edition = string.IsNullOrWhiteSpace(edition) ? "core" : edition.Trim().ToLowerInvariant();
            _startedAt = DateTimeOffset.UtcNow;

            var root = ApplicationData.Current.LocalFolder;
            var core = await root.CreateFolderAsync("Core", CreationCollisionOption.OpenIfExists);
            await core.CreateFolderAsync("Catalogue", CreationCollisionOption.OpenIfExists);
            await core.CreateFolderAsync("Media", CreationCollisionOption.OpenIfExists);
            await core.CreateFolderAsync("Cache", CreationCollisionOption.OpenIfExists);
            await core.CreateFolderAsync("Logs", CreationCollisionOption.OpenIfExists);
            await core.CreateFolderAsync("Ingest", CreationCollisionOption.OpenIfExists);

            _settings.Values["nativeCoreEdition"] = Edition;
            _settings.Values["nativeCoreStarted"] = _startedAt.ToString("o");

            Running = true;

            // Optical probing is local to the sandbox and must not depend on Xbox
            // permitting inbound LAN sockets into the AppContainer.
            await _optical.ProbeAsync();

            // Hardware-validation proof: do not block the couch UI while a whole
            // audio track is read. The service persists its own progress/result.
            _ = _cdRip.AutoRipCurrentDiscAsync();

            _api = new LocalCoreApiServer(BuildHealth, BuildRuntime, BuildOptical);
            try
            {
                await _api.StartAsync();
            }
            catch (Exception ex)
            {
                _apiError = ex.GetType().Name + " 0x" +
                    ex.HResult.ToString("X8") + ": " + ex.Message;
            }
        }

        public JsonObject BuildHealth()
        {
            return new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(true),
                ["ready"] = JsonValue.CreateBooleanValue(Running),
                ["service"] = JsonValue.CreateStringValue("core"),
                ["release"] = JsonValue.CreateStringValue("Beta 1"),
                ["version"] = JsonValue.CreateStringValue("0.2.0-xbox-native"),
                ["platform"] = JsonValue.CreateStringValue("xbox-appcontainer"),
                ["edition"] = JsonValue.CreateStringValue(Edition ?? "core"),
                ["uptime_seconds"] = JsonValue.CreateNumberValue(
                    Math.Max(0, (DateTimeOffset.UtcNow - _startedAt).TotalSeconds))
            };
        }

        public JsonObject BuildRuntime()
        {
            var modules = new JsonArray();
            modules.Add(Module("authority", true, "Native Xbox Core authority scaffold"));
            modules.Add(Module("api", string.IsNullOrWhiteSpace(_apiError),
                string.IsNullOrWhiteSpace(_apiError)
                    ? (_api?.Address ?? "started")
                    : "LAN listener unavailable: " + _apiError));
            modules.Add(Module("storage", true, "ApplicationData/LocalFolder/Core"));
            modules.Add(Module("catalogue", false, "SQLite catalogue migration pending"));
            modules.Add(Module("queue", true, "Playback queue engine available"));
            modules.Add(Module("renderer", true, "MediaPlayer background renderer"));
            modules.Add(Module("sooloos", true, "Direct broker compiled"));
            var optical = _optical.LastResult;
            var opticalStatus = optical.ContainsKey("status") &&
                optical["status"].ValueType == JsonValueType.String
                    ? optical["status"].GetString()
                    : "unknown";
            modules.Add(Module("ingest", opticalStatus == "devices_found",
                "Xbox optical probe: " + opticalStatus));
            modules.Add(Module("providers", false, "Provider migration pending"));

            return new JsonObject
            {
                ["edition"] = JsonValue.CreateStringValue(Edition ?? "core"),
                ["api"] = JsonValue.CreateStringValue(_api?.Address ?? ""),
                ["data_root"] = JsonValue.CreateStringValue("ApplicationData/LocalFolder/Core"),
                ["modules"] = modules
            };
        }

        public JsonObject BuildOptical() => _optical.LastResult;

        static JsonObject Module(string name, bool available, string detail)
        {
            return new JsonObject
            {
                ["name"] = JsonValue.CreateStringValue(name),
                ["available"] = JsonValue.CreateBooleanValue(available),
                ["detail"] = JsonValue.CreateStringValue(detail ?? "")
            };
        }

        public void Dispose()
        {
            Running = false;
            _api?.Dispose();
            _api = null;
        }
    }
}
