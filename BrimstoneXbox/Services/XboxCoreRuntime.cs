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
        DateTimeOffset _startedAt;

        public bool Running { get; private set; }
        public string Edition { get; private set; } = "core";
        public string ApiAddress => _api?.Address;

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

            _api = new LocalCoreApiServer(BuildHealth, BuildRuntime, BuildOptical);
            await _api.StartAsync();

            Running = true;

            // Probe only after the Core API is listening so diagnostics remain reachable
            // even if Xbox blocks raw optical access.
            await _optical.ProbeAsync();
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
            modules.Add(Module("api", _api != null, _api?.Address ?? "stopped"));
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
