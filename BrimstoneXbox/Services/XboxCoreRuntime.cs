using BrimstoneXbox.Models;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace BrimstoneXbox.Services
{
    public sealed class XboxCoreRuntime : IDisposable
    {
        readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
        readonly OpticalProbeService _optical = new OpticalProbeService();
        readonly CdRipService _cdRip = new CdRipService();
        readonly NativeCatalogueService _catalogue = new NativeCatalogueService();
        readonly CoreStackSupervisor _stack;

        LocalCoreApiServer _api;
        DateTimeOffset _startedAt;

        public XboxCoreRuntime()
        {
            _stack = new CoreStackSupervisor(_optical, _cdRip);
        }

        public bool Running { get; private set; }
        public bool Ready => Running && _stack.Ready;
        public string Edition { get; private set; } = "core";
        public string ApiAddress => _api?.Address;

        public Task<JsonObject> ProbeOpticalAsync() => _optical.ProbeAsync();
        public Task<JsonObject> GetRipStatusAsync() => _cdRip.CurrentStatusAsync();
        public Task<JsonObject> RipNowAsync() => _cdRip.AutoRipCurrentDiscAsync();
        public Task<List<CoreAlbum>> GetAlbumsAsync() => _catalogue.GetAlbumsAsync();
        public Task PlayTrackAsync(CoreTrack track) => _catalogue.PlayTrackAsync(track);
        public Task PlayAlbumAsync(CoreAlbum album) => _catalogue.PlayAlbumAsync(album);
        public List<QueueItem> GetQueue() => PlaybackService.Instance.QueueSnapshot();

        public void Control(string action)
        {
            switch ((action ?? "").ToLowerInvariant())
            {
                case "play":
                case "resume":
                    PlaybackService.Instance.Resume();
                    break;
                case "pause":
                    PlaybackService.Instance.Pause();
                    break;
                case "stop":
                    PlaybackService.Instance.Stop();
                    break;
                case "next":
                    PlaybackService.Instance.Next();
                    break;
                case "previous":
                    PlaybackService.Instance.Previous();
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unsupported Xbox Core transport action: " + action);
            }
        }
        public JsonObject BuildStack() => _stack.Snapshot();

        public async Task StartAsync(string edition = "core")
        {
            if (Running) return;

            Edition = string.IsNullOrWhiteSpace(edition)
                ? "core"
                : edition.Trim().ToLowerInvariant();
            _startedAt = DateTimeOffset.UtcNow;

            var root = ApplicationData.Current.LocalFolder;
            var core = await root.CreateFolderAsync("Core", CreationCollisionOption.OpenIfExists);
            await core.CreateFolderAsync("Catalogue", CreationCollisionOption.OpenIfExists);
            await core.CreateFolderAsync("Media", CreationCollisionOption.OpenIfExists);
            await core.CreateFolderAsync("Cache", CreationCollisionOption.OpenIfExists);
            await core.CreateFolderAsync("Logs", CreationCollisionOption.OpenIfExists);
            await core.CreateFolderAsync("Ingest", CreationCollisionOption.OpenIfExists);
            await core.CreateFolderAsync("Stack", CreationCollisionOption.OpenIfExists);

            _settings.Values["nativeCoreEdition"] = Edition;
            _settings.Values["nativeCoreStarted"] = _startedAt.ToString("o");

            _api = new LocalCoreApiServer(
                BuildHealth,
                BuildRuntime,
                BuildOptical,
                BuildStack);

            await _stack.StartAsync(
                () => _api.StartAsync(),
                () => _api.Address);

            Running = true;
        }

        public JsonObject BuildHealth()
        {
            return new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(true),
                ["ready"] = JsonValue.CreateBooleanValue(Ready),
                ["service"] = JsonValue.CreateStringValue("Core"),
                ["release"] = JsonValue.CreateStringValue("Beta 1"),
                ["version"] = JsonValue.CreateStringValue("0.2.0-xbox-native"),
                ["platform"] = JsonValue.CreateStringValue("xbox-appcontainer"),
                ["edition"] = JsonValue.CreateStringValue(Edition ?? "core"),
                ["uptime_seconds"] = JsonValue.CreateNumberValue(
                    Math.Max(0, (DateTimeOffset.UtcNow - _startedAt).TotalSeconds)),
                ["stack"] = _stack.Snapshot()
            };
        }

        public JsonObject BuildRuntime()
        {
            var modules = new JsonArray();
            modules.Add(Module("authority", _stack.Ready,
                "Compose-style Core authority supervised in Xbox AppContainer"));
            modules.Add(Module("storage", true,
                "ApplicationData/LocalFolder/Core"));
            modules.Add(Module("catalogue", true,
                "Embedded Core database compatibility store"));
            modules.Add(Module("queue", true,
                "Playback queue engine available"));
            modules.Add(Module("renderer", true,
                "MediaPlayer background renderer"));
            modules.Add(Module("sooloos", true,
                "Direct Meridian/Sooloos broker compiled"));

            var optical = _optical.LastResult;
            var opticalStatus = optical.ContainsKey("status") &&
                optical["status"].ValueType == JsonValueType.String
                    ? optical["status"].GetString()
                    : "unknown";
            modules.Add(Module("ingest", opticalStatus == "devices_found",
                "Xbox optical probe: " + opticalStatus));
            modules.Add(Module("providers", false,
                "Provider migration in progress"));

            return new JsonObject
            {
                ["edition"] = JsonValue.CreateStringValue(Edition ?? "core"),
                ["api"] = JsonValue.CreateStringValue(_api?.Address ?? ""),
                ["data_root"] = JsonValue.CreateStringValue(
                    "ApplicationData/LocalFolder/Core"),
                ["ready"] = JsonValue.CreateBooleanValue(Ready),
                ["modules"] = modules,
                ["stack"] = _stack.Snapshot()
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
            _stack.Dispose();
            _api?.Dispose();
            _api = null;
        }
    }
}
