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
        readonly BluRayAudioService _bluRay = new BluRayAudioService();
        readonly NativeCatalogueService _catalogue = new NativeCatalogueService();
        readonly CoreStackSupervisor _stack;

        LocalCoreApiServer _api;
        DateTimeOffset _startedAt;
        string _apiToken;

        public XboxCoreRuntime()
        {
            _stack = new CoreStackSupervisor(_optical, _cdRip, _bluRay);
        }

        public bool Running { get; private set; }
        public bool Ready => Running && _stack.Ready;
        public string Edition { get; private set; } = "core";
        public string ApiAddress => _api?.FriendlyAddress ?? _api?.Address;

        public Task<JsonObject> ProbeOpticalAsync() => _optical.ProbeAsync();
        public Task<JsonObject> GetRipStatusAsync() => _cdRip.CurrentStatusAsync();
        public Task<JsonObject> RipNowAsync() => _cdRip.AutoRipCurrentDiscAsync();
        public Task<JsonObject> ScanBluRayAsync() => _bluRay.ScanAsync();
        public Task<JsonObject> CreateBluRayRipPlanAsync(string playlist, bool keepVideo) =>
            _bluRay.CreateRipPlanAsync(playlist, keepVideo);
        public Task<JsonObject> RipBluRayTitleAsync(string playlist, bool keepVideo) =>
            _bluRay.RipSelectedTitleAsync(playlist, keepVideo);
        public Task<JsonObject> GetBluRayRipStatusAsync() =>
            _bluRay.CurrentRipStatusAsync();
        public Task<List<CoreAlbum>> GetAlbumsAsync() => _catalogue.GetAlbumsAsync();
        public Task PlayTrackAsync(CoreTrack track) => _catalogue.PlayTrackAsync(track);
        public Task PlayAlbumAsync(CoreAlbum album) => _catalogue.PlayAlbumAsync(album);
        public bool ToggleFavourite(CoreAlbum album)
        {
            if (album == null) return false;
            album.IsFavourite = _catalogue.ToggleFavourite(album.Id);
            return album.IsFavourite;
        }
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
            EnsureApiToken();

            _api = new LocalCoreApiServer(this);

            await _stack.StartAsync(
                () => _api.StartAsync(),
                () => _api.Address);

            Running = true;
        }

        void EnsureApiToken()
        {
            object saved;
            if (_settings.Values.TryGetValue("nativeCoreApiToken", out saved))
                _apiToken = saved as string;

            if (string.IsNullOrWhiteSpace(_apiToken))
            {
                _apiToken = Guid.NewGuid().ToString("N") +
                            Guid.NewGuid().ToString("N");
                _settings.Values["nativeCoreApiToken"] = _apiToken;
            }
        }

        public JsonObject Login(string username, string password)
        {
            EnsureApiToken();

            object savedPassword;
            var expectedPassword = _settings.Values.TryGetValue(
                "nativeCoreAdminPassword", out savedPassword)
                ? savedPassword as string
                : null;

            if (string.IsNullOrWhiteSpace(expectedPassword))
                expectedPassword = "password";

            var validUser = string.Equals(
                string.IsNullOrWhiteSpace(username) ? "admin" : username.Trim(),
                "admin",
                StringComparison.OrdinalIgnoreCase);

            if (!validUser || !string.Equals(password ?? "", expectedPassword, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Invalid Core username or password.");

            var user = BuildAdminUser();
            return new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(true),
                ["token"] = JsonValue.CreateStringValue(_apiToken),
                ["expires_at"] = JsonValue.CreateStringValue(""),
                ["username"] = JsonValue.CreateStringValue("admin"),
                ["role"] = JsonValue.CreateStringValue("admin"),
                ["user"] = user
            };
        }

        public JsonObject BuildAuthState()
        {
            return new JsonObject
            {
                ["configured"] = JsonValue.CreateBooleanValue(true),
                ["password_auth"] = JsonValue.CreateBooleanValue(true),
                ["bundled_admin"] = JsonValue.CreateBooleanValue(true)
            };
        }

        public JsonObject BuildAuthMe()
        {
            return new JsonObject
            {
                ["user"] = BuildAdminUser()
            };
        }

        JsonObject BuildAdminUser()
        {
            return new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue("admin"),
                ["username"] = JsonValue.CreateStringValue("admin"),
                ["display_name"] = JsonValue.CreateStringValue("Administrator"),
                ["role"] = JsonValue.CreateStringValue("admin"),
                ["enabled"] = JsonValue.CreateBooleanValue(true),
                ["master"] = JsonValue.CreateBooleanValue(true)
            };
        }

        public bool IsAuthorized(string bearer)
        {
            EnsureApiToken();
            return !string.IsNullOrWhiteSpace(bearer) &&
                   string.Equals(bearer, _apiToken, StringComparison.Ordinal);
        }

        public Task<JsonObject> BuildCatalogApiAsync() =>
            _catalogue.BuildAlbumsApiAsync();

        public Task PlayMediaIdAsync(long id) =>
            _catalogue.PlayTrackIdAsync(id);

        public Task PlayProgrammeIdsAsync(System.Collections.Generic.IList<long> ids) =>
            _catalogue.PlayProgrammeIdsAsync(ids);

        public JsonObject BuildAudioOutput()
        {
            var playback = PlaybackService.Instance;
            return new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue(playback.AudioOutputId ?? ""),
                ["name"] = JsonValue.CreateStringValue(playback.AudioOutputName ?? "Xbox system output"),
                ["policy"] = JsonValue.CreateStringValue("hdmi-optical-auto")
            };
        }

        public async Task<JsonObject> BuildAudioOutputsAsync()
        {
            var rows = new JsonArray();
            rows.Add(new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue("auto"),
                ["name"] = JsonValue.CreateStringValue("Automatic · Xbox HDMI + Optical mirror"),
                ["preferred"] = JsonValue.CreateBooleanValue(true),
                ["active"] = JsonValue.CreateBooleanValue(false)
            });

            foreach (var row in await PlaybackService.Instance.GetAudioOutputsAsync())
                rows.Add(row);

            return new JsonObject
            {
                ["policy"] = JsonValue.CreateStringValue("hdmi-optical-auto"),
                ["current"] = BuildAudioOutput(),
                ["outputs"] = rows
            };
        }

        public async Task<JsonObject> SetAudioOutputAsync(string id)
        {
            await PlaybackService.Instance.SetAudioOutputAsync(id);
            return BuildAudioOutput();
        }

        public JsonObject BuildEndpoints()
        {
            var endpoints = new JsonArray();
            endpoints.Add(BuildEndpoint());
            return new JsonObject { ["endpoints"] = endpoints };
        }

        public JsonObject BuildEndpoint()
        {
            var p = PlaybackService.Instance.Snapshot();

            var controls = new JsonArray();
            foreach (var value in new[]
            {
                "play", "resume", "pause", "stop",
                "next", "previous", "volume", "mute"
            })
                controls.Add(JsonValue.CreateStringValue(value));

            var transports = new JsonArray();
            transports.Add(JsonValue.CreateStringValue("pcm"));

            var playback = PlaybackService.Instance;
            var devices = new JsonArray();
            devices.Add(new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue(
                    string.IsNullOrWhiteSpace(playback.AudioOutputId)
                        ? "auto"
                        : playback.AudioOutputId),
                ["name"] = JsonValue.CreateStringValue(
                    playback.AudioOutputName ?? "HDMI / Optical (Xbox default)"),
                ["default"] = JsonValue.CreateBooleanValue(true),
                ["policy"] = JsonValue.CreateStringValue("hdmi-optical-auto")
            });

            return new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue(XboxIdentity.EndpointId),
                ["name"] = JsonValue.CreateStringValue("Xbox"),
                ["kind"] = JsonValue.CreateStringValue("coreaudio"),
                ["address"] = JsonValue.CreateStringValue(ApiAddress ?? ""),
                ["online"] = JsonValue.CreateBooleanValue(true),
                ["state"] = JsonValue.CreateStringValue(p.State ?? "idle"),
                ["software_version"] = JsonValue.CreateStringValue("0.2.18"),
                ["capabilities"] = new JsonObject
                {
                    ["local_playback"] = JsonValue.CreateBooleanValue(true),
                    ["optical_rip"] = JsonValue.CreateBooleanValue(true),
                    ["queue"] = JsonValue.CreateBooleanValue(true),
                    ["programme"] = JsonValue.CreateBooleanValue(true),
                    ["control_api"] = JsonValue.CreateStringValue("xbox-native-v1"),
                    ["controls"] = controls,
                    ["transports"] = transports,
                    ["devices"] = devices
                }
            };
        }

        public JsonObject BuildZones()
        {
            var endpoint = BuildEndpoint();
            var transports = new JsonArray();
            transports.Add(JsonValue.CreateStringValue("coreaudio"));

            var zone = new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue("xbox-local"),
                ["name"] = JsonValue.CreateStringValue("Xbox"),
                ["endpoint_id"] = JsonValue.CreateStringValue(XboxIdentity.EndpointId),
                ["address"] = JsonValue.CreateStringValue(ApiAddress ?? ""),
                ["transport"] = JsonValue.CreateStringValue("coreaudio"),
                ["transport_key"] = JsonValue.CreateStringValue(XboxIdentity.EndpointId),
                ["transports"] = transports,
                ["online"] = JsonValue.CreateBooleanValue(true),
                ["endpoint"] = endpoint
            };

            var zones = new JsonArray();
            zones.Add(zone);
            return new JsonObject { ["zones"] = zones };
        }

        public JsonObject BuildEndpointStatus()
        {
            var p = PlaybackService.Instance.Snapshot();
            var queue = new JsonArray();
            foreach (var item in PlaybackService.Instance.QueueSnapshot())
            {
                queue.Add(new JsonObject
                {
                    ["title"] = JsonValue.CreateStringValue(item.Title ?? ""),
                    ["artist"] = JsonValue.CreateStringValue(item.Artist ?? ""),
                    ["album"] = JsonValue.CreateStringValue(item.Album ?? ""),
                    ["duration"] = JsonValue.CreateNumberValue(item.DurationSeconds)
                });
            }

            return new JsonObject
            {
                ["id"] = JsonValue.CreateStringValue(XboxIdentity.EndpointId),
                ["name"] = JsonValue.CreateStringValue("Xbox"),
                ["state"] = JsonValue.CreateStringValue(p.State ?? "idle"),
                ["playing"] = JsonValue.CreateBooleanValue(p.Playing),
                ["title"] = JsonValue.CreateStringValue(p.Title ?? ""),
                ["artist"] = JsonValue.CreateStringValue(p.Artist ?? ""),
                ["album"] = JsonValue.CreateStringValue(p.Album ?? ""),
                ["position_seconds"] = JsonValue.CreateNumberValue(p.PositionSeconds),
                ["duration_seconds"] = JsonValue.CreateNumberValue(p.DurationSeconds),
                ["volume"] = JsonValue.CreateNumberValue(p.Volume),
                ["muted"] = JsonValue.CreateBooleanValue(p.Muted),
                ["output_id"] = JsonValue.CreateStringValue(
                    PlaybackService.Instance.AudioOutputId ?? ""),
                ["output_name"] = JsonValue.CreateStringValue(
                    PlaybackService.Instance.AudioOutputName ?? "Xbox system output"),
                ["queue"] = queue
            };
        }

        public void SetVolume(double percent)
        {
            PlaybackService.Instance.SetVolume(percent);
        }

        public void SetMuted(bool muted)
        {
            PlaybackService.Instance.SetMuted(muted);
        }

        public JsonObject BuildIngestCapabilities()
        {
            return new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(true),
                ["auto_rip"] = JsonValue.CreateBooleanValue(true),
                ["raw_cdda"] = JsonValue.CreateBooleanValue(true),
                ["eject"] = JsonValue.CreateBooleanValue(true),
                ["bluray_auto_rip"] = JsonValue.CreateBooleanValue(true),
                ["bluray_mpls_titles"] = JsonValue.CreateBooleanValue(true),
                ["bluray_source_stage"] = JsonValue.CreateBooleanValue(true),
                ["mkv_rip_plan"] = JsonValue.CreateBooleanValue(true),
                ["makemkv_helper_contract"] = JsonValue.CreateBooleanValue(true),
                ["platform"] = JsonValue.CreateStringValue("xbox-customdevice")
            };
        }

        public JsonObject BuildIngestDevices()
        {
            var optical = new JsonArray();
            var probe = _optical.LastResult;
            if (probe != null &&
                probe.ContainsKey("devices") &&
                probe["devices"].ValueType == JsonValueType.Array)
            {
                foreach (var item in probe.GetNamedArray("devices"))
                    optical.Add(item);
            }

            return new JsonObject
            {
                ["optical"] = optical
            };
        }

        public async Task<JsonObject> BuildIngestJobsAsync()
        {
            var jobs = new JsonArray();
            var status = await _cdRip.CurrentStatusAsync();
            var state = status.ContainsKey("state") &&
                        status["state"].ValueType == JsonValueType.String
                ? status["state"].GetString()
                : "idle";

            if (state != "idle")
            {
                var job = JsonObject.Parse(status.Stringify());
                job["label"] = JsonValue.CreateStringValue("Xbox CD");
                jobs.Add(job);
            }

            var bluRayStatus = await _bluRay.CurrentRipStatusAsync();
            var bluRayState = bluRayStatus.ContainsKey("state") &&
                               bluRayStatus["state"].ValueType == JsonValueType.String
                ? bluRayStatus["state"].GetString()
                : "idle";
            if (bluRayState != "idle" && bluRayState != "not_run")
            {
                var job = JsonObject.Parse(bluRayStatus.Stringify());
                job["label"] = JsonValue.CreateStringValue("Xbox Blu-ray Audio");
                jobs.Add(job);
            }

            return new JsonObject
            {
                ["jobs"] = jobs
            };
        }

        public JsonObject BuildHealth()
        {
            return new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(true),
                ["ready"] = JsonValue.CreateBooleanValue(Ready),
                ["service"] = JsonValue.CreateStringValue("Core"),
                ["release"] = JsonValue.CreateStringValue("Beta 1"),
                ["version"] = JsonValue.CreateStringValue("0.2.18-xbox-native"),
                ["platform"] = JsonValue.CreateStringValue("xbox-appcontainer"),
                ["hostname"] = JsonValue.CreateStringValue(LocalCoreApiServer.PreferredHostName),
                ["edition"] = JsonValue.CreateStringValue(Edition ?? "core"),
                ["uptime_seconds"] = JsonValue.CreateNumberValue(
                    Math.Max(0, (DateTimeOffset.UtcNow - _startedAt).TotalSeconds)),
                ["stack"] = _stack.Snapshot(),
                ["discovery"] = new JsonObject
                {
                    ["service_type"] = JsonValue.CreateStringValue("_brimstone-core._tcp.local."),
                    ["status"] = JsonValue.CreateStringValue(_api == null ? "not_started" : _api.DiscoveryStatus),
                    ["instance"] = JsonValue.CreateStringValue(_api == null ? "" : _api.DiscoveryInstanceName)
                }
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
