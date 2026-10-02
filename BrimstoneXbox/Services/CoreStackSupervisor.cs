using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace BrimstoneXbox.Services
{
    public sealed class CoreStackSupervisor
    {
        readonly OpticalProbeService _optical;
        readonly CdRipService _rip;
        readonly Dictionary<string, StackServiceState> _services =
            new Dictionary<string, StackServiceState>(StringComparer.OrdinalIgnoreCase);

        StorageFolder _stackFolder;
        Func<Task> _startWeb;
        Func<string> _webAddress;

        public CoreStackSupervisor(OpticalProbeService optical, CdRipService rip)
        {
            _optical = optical ?? throw new ArgumentNullException(nameof(optical));
            _rip = rip ?? throw new ArgumentNullException(nameof(rip));

            Add("core-postgres", "postgres:18-trixie", "", "unless-stopped");
            Add("core-cast", "core-cast:local", "", "unless-stopped");
            Add("surroundcore", "core:debian13", "core-postgres", "unless-stopped");
            Add("surroundcore-web", "caddy:2-alpine", "surroundcore", "unless-stopped");
            Add("surround-ingest", "ingest:debian13", "surroundcore", "unless-stopped");
        }

        public bool Ready =>
            Healthy("core-postgres") &&
            Healthy("surroundcore") &&
            Healthy("surround-ingest");

        public async Task StartAsync(Func<Task> startWeb, Func<string> webAddress)
        {
            _startWeb = startWeb;
            _webAddress = webAddress;

            var root = ApplicationData.Current.LocalFolder;
            var core = await root.CreateFolderAsync("Core", CreationCollisionOption.OpenIfExists);
            _stackFolder = await core.CreateFolderAsync("Stack", CreationCollisionOption.OpenIfExists);

            await StartDatabaseAsync();
            await StartCastAsync();
            await StartCoreAuthorityAsync();
            await StartWebAsync();
            await StartIngestAsync();
            await PersistAsync();
        }

        async Task StartDatabaseAsync()
        {
            var state = Service("core-postgres");
            Starting(state, "Starting embedded catalogue authority");

            try
            {
                var root = ApplicationData.Current.LocalFolder;
                var core = await root.CreateFolderAsync("Core", CreationCollisionOption.OpenIfExists);
                var catalogue = await core.CreateFolderAsync("Catalogue", CreationCollisionOption.OpenIfExists);
                var db = await catalogue.CreateFileAsync(
                    "core-database.json",
                    CreationCollisionOption.OpenIfExists);

                if ((await db.GetBasicPropertiesAsync()).Size == 0)
                {
                    var initial = new JsonObject
                    {
                        ["schema"] = JsonValue.CreateNumberValue(1),
                        ["backend"] = JsonValue.CreateStringValue("xbox-embedded"),
                        ["created"] = JsonValue.CreateStringValue(DateTimeOffset.UtcNow.ToString("o")),
                        ["albums"] = new JsonArray(),
                        ["tracks"] = new JsonArray(),
                        ["sources"] = new JsonArray(),
                        ["users"] = new JsonArray()
                    };
                    await FileIO.WriteTextAsync(db, initial.Stringify());
                }

                Running(state,
                    "Embedded Core database · LocalState/Core/Catalogue/core-database.json",
                    "xbox-embedded-db");
            }
            catch (Exception ex)
            {
                Failed(state, ex);
            }

            await PersistAsync();
        }

        Task StartCastAsync()
        {
            var state = Service("core-cast");
            Starting(state, "Starting native Xbox playback/cast broker");

            try
            {
                var playback = PlaybackService.Instance;
                Running(state,
                    "MediaPlayer renderer + Sooloos broker available",
                    "xbox-media-broker");
            }
            catch (Exception ex)
            {
                Failed(state, ex);
            }

            return PersistAsync();
        }

        Task StartCoreAuthorityAsync()
        {
            var state = Service("surroundcore");
            Starting(state, "Starting native Core authority");

            if (!Healthy("core-postgres"))
            {
                Blocked(state, "core-postgres is not healthy");
                return PersistAsync();
            }

            Running(state,
                "Core authority running inside Brimstone AppContainer",
                "xbox-native-core");
            return PersistAsync();
        }

        async Task StartWebAsync()
        {
            var state = Service("surroundcore-web");
            Starting(state, "Starting private Core HTTP surface");

            if (!Healthy("surroundcore"))
            {
                Blocked(state, "surroundcore is not healthy");
                await PersistAsync();
                return;
            }

            try
            {
                if (_startWeb != null)
                    await _startWeb();

                var address = _webAddress == null ? "" : _webAddress() ?? "";
                Running(state,
                    string.IsNullOrWhiteSpace(address)
                        ? "Internal HTTP surface started"
                        : "Core HTTP surface · " + address,
                    "xbox-streamsocket");
            }
            catch (Exception ex)
            {
                // LAN listeners may be brokered differently on Xbox. The TV shell
                // and the rest of Core remain usable, so this is degraded not fatal.
                Degraded(state,
                    ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") +
                    ": " + ex.Message,
                    "xbox-streamsocket");
            }

            await PersistAsync();
        }

        async Task StartIngestAsync()
        {
            var state = Service("surround-ingest");
            Starting(state, "Starting Xbox optical ingest service");

            if (!Healthy("surroundcore"))
            {
                Blocked(state, "surroundcore is not healthy");
                await PersistAsync();
                return;
            }

            try
            {
                var probe = await _optical.ProbeAsync();
                var probeStatus = GetString(probe, "status");
                var deviceCount = GetNumber(probe, "device_count");

                if (probeStatus == "devices_found" && deviceCount > 0)
                {
                    Running(state,
                        "Xbox optical ingest · " + deviceCount.ToString("0") +
                        " CD-ROM interface(s) · raw CDDA enabled",
                        "xbox-customdevice");
                    _ = _rip.AutoRipCurrentDiscAsync();
                }
                else
                {
                    Degraded(state,
                        "Optical service running; no accessible disc interface at startup (" +
                        (string.IsNullOrWhiteSpace(probeStatus) ? "unknown" : probeStatus) + ")",
                        "xbox-customdevice");
                }
            }
            catch (Exception ex)
            {
                Failed(state, ex);
            }

            await PersistAsync();
        }

        public JsonObject Snapshot()
        {
            var services = new JsonArray();
            foreach (var name in new[]
            {
                "core-postgres",
                "core-cast",
                "surroundcore",
                "surroundcore-web",
                "surround-ingest"
            })
            {
                services.Add(Service(name).ToJson());
            }

            return new JsonObject
            {
                ["name"] = JsonValue.CreateStringValue("brimstone-core"),
                ["runtime"] = JsonValue.CreateStringValue("xbox-appcontainer-compose"),
                ["ready"] = JsonValue.CreateBooleanValue(Ready),
                ["service_count"] = JsonValue.CreateNumberValue(services.Count),
                ["services"] = services
            };
        }

        public async Task RestartAsync(string name)
        {
            if (!_services.ContainsKey(name))
                throw new InvalidOperationException("Unknown Core stack service: " + name);

            switch (name.ToLowerInvariant())
            {
                case "core-postgres":
                    await StartDatabaseAsync();
                    break;
                case "core-cast":
                    await StartCastAsync();
                    break;
                case "surroundcore":
                    await StartCoreAuthorityAsync();
                    break;
                case "surroundcore-web":
                    await StartWebAsync();
                    break;
                case "surround-ingest":
                    await StartIngestAsync();
                    break;
            }
        }

        void Add(string name, string image, string dependsOn, string restart)
        {
            _services[name] = new StackServiceState
            {
                Name = name,
                Image = image,
                DependsOn = dependsOn,
                RestartPolicy = restart,
                State = "created",
                Health = "starting",
                Implementation = "xbox-native"
            };
        }

        StackServiceState Service(string name) => _services[name];

        bool Healthy(string name)
        {
            var state = Service(name);
            return state.State == "running" &&
                (state.Health == "healthy" || state.Health == "degraded");
        }

        static void Starting(StackServiceState state, string detail)
        {
            state.State = "starting";
            state.Health = "starting";
            state.Detail = detail;
            state.Error = "";
            state.StartedAt = DateTimeOffset.UtcNow;
        }

        static void Running(StackServiceState state, string detail, string implementation)
        {
            state.State = "running";
            state.Health = "healthy";
            state.Detail = detail;
            state.Error = "";
            state.Implementation = implementation;
            state.StartedAt = state.StartedAt == default(DateTimeOffset)
                ? DateTimeOffset.UtcNow
                : state.StartedAt;
        }

        static void Degraded(StackServiceState state, string detail, string implementation)
        {
            state.State = "running";
            state.Health = "degraded";
            state.Detail = detail;
            state.Error = detail;
            state.Implementation = implementation;
            state.StartedAt = state.StartedAt == default(DateTimeOffset)
                ? DateTimeOffset.UtcNow
                : state.StartedAt;
        }

        static void Failed(StackServiceState state, Exception ex)
        {
            state.State = "exited";
            state.Health = "unhealthy";
            state.Error = ex.GetType().Name + " 0x" +
                ex.HResult.ToString("X8") + ": " + ex.Message;
            state.Detail = state.Error;
        }

        static void Blocked(StackServiceState state, string reason)
        {
            state.State = "blocked";
            state.Health = "unhealthy";
            state.Error = reason;
            state.Detail = reason;
        }

        async Task PersistAsync()
        {
            if (_stackFolder == null) return;

            try
            {
                var file = await _stackFolder.CreateFileAsync(
                    "compose-state.json",
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, Snapshot().Stringify());
            }
            catch
            {
                // Stack state is diagnostic. Persistence must not stop Core.
            }
        }

        static string GetString(JsonObject obj, string key)
        {
            return obj != null && obj.ContainsKey(key) &&
                   obj[key].ValueType == JsonValueType.String
                ? obj[key].GetString()
                : "";
        }

        static double GetNumber(JsonObject obj, string key)
        {
            return obj != null && obj.ContainsKey(key) &&
                   obj[key].ValueType == JsonValueType.Number
                ? obj[key].GetNumber()
                : 0;
        }

        sealed class StackServiceState
        {
            public string Name;
            public string Image;
            public string DependsOn;
            public string RestartPolicy;
            public string State;
            public string Health;
            public string Implementation;
            public string Detail;
            public string Error;
            public DateTimeOffset StartedAt;

            public JsonObject ToJson()
            {
                return new JsonObject
                {
                    ["name"] = JsonValue.CreateStringValue(Name ?? ""),
                    ["image"] = JsonValue.CreateStringValue(Image ?? ""),
                    ["depends_on"] = JsonValue.CreateStringValue(DependsOn ?? ""),
                    ["restart"] = JsonValue.CreateStringValue(RestartPolicy ?? ""),
                    ["state"] = JsonValue.CreateStringValue(State ?? ""),
                    ["health"] = JsonValue.CreateStringValue(Health ?? ""),
                    ["implementation"] = JsonValue.CreateStringValue(Implementation ?? ""),
                    ["detail"] = JsonValue.CreateStringValue(Detail ?? ""),
                    ["error"] = JsonValue.CreateStringValue(Error ?? ""),
                    ["started_at"] = JsonValue.CreateStringValue(
                        StartedAt == default(DateTimeOffset) ? "" : StartedAt.ToString("o"))
                };
            }
        }
    }
}
