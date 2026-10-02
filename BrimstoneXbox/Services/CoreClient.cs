using BrimstoneXbox.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Streams;

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
                ["software_version"] = JsonValue.CreateStringValue("0.2.18"),
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

        public async Task<List<QueueItem>> GetQueueAsync(string endpointId)
        {
            RequireUserToken();
            var json = await SendAsync(
                HttpMethod.Get,
                "/api/v1/endpoints/" + Uri.EscapeDataString(endpointId) + "/status",
                null,
                UserToken);
            var root = JsonObject.Parse(json);
            var result = new List<QueueItem>();
            if (!root.ContainsKey("queue") || root["queue"].ValueType != JsonValueType.Array)
                return result;

            var index = 1;
            foreach (var value in root.GetNamedArray("queue"))
            {
                if (value.ValueType != JsonValueType.Object) continue;
                var item = value.GetObject();
                result.Add(new QueueItem
                {
                    Number = index++.ToString(),
                    Title = StringValue(item, "title", "Track"),
                    Artist = StringValue(item, "artist"),
                    Album = StringValue(item, "album"),
                    DurationSeconds = NumberValue(item, "duration", 0)
                });
            }
            return result;
        }

        public async Task<JsonObject> ImportAlbumAsync(CoreAlbum album)
        {
            RequireUserToken();
            if (album == null || album.Tracks == null || album.Tracks.Count == 0)
                throw new InvalidOperationException("This album has no Xbox-local tracks to send.");

            var results = new JsonArray();
            var imported = 0;
            var alreadyPresent = 0;

            foreach (var track in album.Tracks)
            {
                if (track == null || string.IsNullOrWhiteSpace(track.LocalPath))
                    continue;

                var absolute = System.IO.Path.Combine(
                    ApplicationData.Current.LocalFolder.Path,
                    track.LocalPath);
                var file = await StorageFile.GetFileFromPathAsync(absolute);
                var sha = await Sha256Async(file);
                var props = await file.GetBasicPropertiesAsync();

                var target = BaseUrl.TrimEnd('/') +
                    "/api/v1/library/import?artist=" +
                    Uri.EscapeDataString(album.Artist ?? "Unknown Artist") +
                    "&album=" + Uri.EscapeDataString(album.Title ?? "Unknown Album") +
                    "&filename=" + Uri.EscapeDataString(file.Name ?? "track.wav") +
                    "&provenance=" + Uri.EscapeDataString("xbox-core") +
                    "&edition=" + Uri.EscapeDataString("Xbox rip");

                using (var request = new HttpRequestMessage(HttpMethod.Put, new Uri(target)))
                using (var content = await StorageFileHttpContent.CreateAsync(file))
                {
                    request.Headers.Accept.Add(
                        new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", UserToken);
                    request.Headers.TryAddWithoutValidation(
                        "X-Content-SHA256", sha);
                    content.Headers.ContentType =
                        new MediaTypeHeaderValue("application/octet-stream");
                    content.Headers.ContentLength = (long)props.Size;
                    request.Content = content;

                    var response = await _http.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        var message = "Core import failed (" +
                            (int)response.StatusCode + ").";
                        try
                        {
                            var error = JsonObject.Parse(body);
                            message = StringValue(error, "detail",
                                StringValue(error, "error", message));
                        }
                        catch { }
                        throw new InvalidOperationException(message);
                    }

                    var parsed = JsonObject.Parse(
                        string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    var serverSha = StringValue(parsed, "sha256", "");
                    if (!string.IsNullOrWhiteSpace(serverSha) &&
                        !string.Equals(serverSha, sha,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "Destination Core checksum did not match " + file.Name + ".");

                    var existing = parsed.ContainsKey("already_present") &&
                                   parsed["already_present"].ValueType == JsonValueType.Boolean &&
                                   parsed["already_present"].GetBoolean();
                    if (existing) alreadyPresent++;
                    else imported++;

                    results.Add(new JsonObject
                    {
                        ["file"] = JsonValue.CreateStringValue(file.Name ?? ""),
                        ["sha256"] = JsonValue.CreateStringValue(
                            string.IsNullOrWhiteSpace(serverSha) ? sha : serverSha),
                        ["already_present"] = JsonValue.CreateBooleanValue(existing)
                    });
                }
            }

            return new JsonObject
            {
                ["ok"] = JsonValue.CreateBooleanValue(true),
                ["album"] = JsonValue.CreateStringValue(album.Title ?? ""),
                ["artist"] = JsonValue.CreateStringValue(album.Artist ?? ""),
                ["imported"] = JsonValue.CreateNumberValue(imported),
                ["already_present"] = JsonValue.CreateNumberValue(alreadyPresent),
                ["files"] = results
            };
        }

        public async Task<JsonObject> ImportStagedBluRayAsync(
            JsonObject ripStatus)
        {
            RequireUserToken();
            if (ripStatus == null ||
                !string.Equals(
                    StringValue(ripStatus, "state", ""),
                    "source_staged",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "No staged Xbox Blu-ray title is ready to send.");

            var manifestRelative = StringValue(ripStatus, "manifest", "");
            if (string.IsNullOrWhiteSpace(manifestRelative))
                throw new InvalidOperationException(
                    "The staged Blu-ray rip has no title manifest.");

            var root = ApplicationData.Current.LocalFolder;
            var manifestPath = System.IO.Path.Combine(
                root.Path,
                manifestRelative.Replace(
                    '/',
                    System.IO.Path.DirectorySeparatorChar));
            var manifestFile =
                await StorageFile.GetFileFromPathAsync(manifestPath);
            var manifestText = await FileIO.ReadTextAsync(manifestFile);

            JsonObject manifest;
            if (!JsonObject.TryParse(manifestText, out manifest))
                throw new InvalidOperationException(
                    "The staged Blu-ray title manifest is invalid.");

            var sourceFolderRelative = StringValue(
                manifest,
                "source_folder",
                "");
            if (string.IsNullOrWhiteSpace(sourceFolderRelative))
                throw new InvalidOperationException(
                    "The Blu-ray title manifest has no staged source folder.");

            var sourceFolderPath = System.IO.Path.Combine(
                root.Path,
                sourceFolderRelative.Replace(
                    '/',
                    System.IO.Path.DirectorySeparatorChar));
            var sourceFolder =
                await StorageFolder.GetFolderFromPathAsync(sourceFolderPath);

            if (!manifest.ContainsKey("staged_files") ||
                manifest["staged_files"].ValueType != JsonValueType.Array)
                throw new InvalidOperationException(
                    "The Blu-ray title manifest has no staged files.");

            var staged = new List<BluRayTransferFile>();
            foreach (var value in manifest.GetNamedArray("staged_files"))
            {
                if (value.ValueType != JsonValueType.Object)
                    continue;

                var row = value.GetObject();
                var sourceName = StringValue(row, "source", "");
                var storedName = StringValue(row, "file", "");
                if (string.IsNullOrWhiteSpace(sourceName) ||
                    string.IsNullOrWhiteSpace(storedName))
                    continue;

                staged.Add(new BluRayTransferFile
                {
                    SourceName = sourceName,
                    File = await sourceFolder.GetFileAsync(storedName)
                });
            }

            if (staged.Count == 0)
                throw new InvalidOperationException(
                    "No staged Blu-ray source clips were available.");

            var modular = await TryImportStagedBluRayModularAsync(
                manifest,
                staged);
            if (modular != null)
                return modular;

            return await ImportStagedBluRayLegacyAsync(
                manifest,
                staged);
        }

        async Task<JsonObject> TryImportStagedBluRayModularAsync(
            JsonObject manifest,
            IList<BluRayTransferFile> staged)
        {
            var baseUrl = BaseUrl.TrimEnd('/');
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromHours(4);

                JsonObject created;
                using (var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(baseUrl +
                        "/api/v1/ingest/bluray-programmes")))
                {
                    request.Headers.Accept.Add(
                        new MediaTypeWithQualityHeaderValue(
                            "application/json"));
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue(
                            "Bearer",
                            UserToken);
                    request.Content = new StringContent(
                        manifest.Stringify(),
                        Encoding.UTF8,
                        "application/json");

                    var response = await client.SendAsync(request);
                    if (response.StatusCode ==
                            System.Net.HttpStatusCode.NotFound ||
                        response.StatusCode ==
                            System.Net.HttpStatusCode.MethodNotAllowed)
                        return null;

                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            CoreTransferError(
                                body,
                                "Core Blu-ray job creation failed (" +
                                (int)response.StatusCode + ")."));

                    created = JsonObject.Parse(
                        string.IsNullOrWhiteSpace(body)
                            ? "{}"
                            : body);
                }

                var jobId = StringValue(created, "job_id", "");
                if (string.IsNullOrWhiteSpace(jobId))
                    throw new InvalidOperationException(
                        "Destination Core did not return a Blu-ray job id.");

                foreach (var stagedFile in staged)
                {
                    var path =
                        "/api/v1/ingest/bluray-programmes/" +
                        Uri.EscapeDataString(jobId) +
                        "/clips/" +
                        Uri.EscapeDataString(stagedFile.SourceName);

                    using (var request = new HttpRequestMessage(
                        HttpMethod.Put,
                        new Uri(baseUrl + path)))
                    using (var content =
                        await StorageFileHttpContent.CreateAsync(
                            stagedFile.File))
                    {
                        request.Headers.Accept.Add(
                            new MediaTypeWithQualityHeaderValue(
                                "application/json"));
                        request.Headers.Authorization =
                            new AuthenticationHeaderValue(
                                "Bearer",
                                UserToken);
                        content.Headers.ContentType =
                            new MediaTypeHeaderValue(
                                "application/octet-stream");
                        request.Content = content;

                        var response = await client.SendAsync(request);
                        var body =
                            await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException(
                                CoreTransferError(
                                    body,
                                    "Core rejected Blu-ray clip " +
                                    stagedFile.SourceName +
                                    " (" +
                                    (int)response.StatusCode +
                                    ")."));
                    }
                }

                using (var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(
                        baseUrl +
                        "/api/v1/ingest/bluray-programmes/" +
                        Uri.EscapeDataString(jobId) +
                        "/finalize")))
                {
                    request.Headers.Accept.Add(
                        new MediaTypeWithQualityHeaderValue(
                            "application/json"));
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue(
                            "Bearer",
                            UserToken);

                    var response = await client.SendAsync(request);
                    var body =
                        await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            CoreTransferError(
                                body,
                                "Core Blu-ray finalization failed (" +
                                (int)response.StatusCode + ")."));

                    var final = JsonObject.Parse(
                        string.IsNullOrWhiteSpace(body)
                            ? "{}"
                            : body);
                    if (!string.Equals(
                        StringValue(final, "state", ""),
                        "completed",
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "Core did not confirm the Blu-ray ingest.");

                    var normalized = new JsonObject
                    {
                        ["ok"] = JsonValue.CreateBooleanValue(true),
                        ["backend"] =
                            JsonValue.CreateStringValue(
                                "modular-core"),
                        ["job_id"] =
                            JsonValue.CreateStringValue(jobId)
                    };
                    if (final.ContainsKey("result") &&
                        final["result"].ValueType ==
                            JsonValueType.Object)
                        normalized["result"] =
                            final.GetNamedObject("result");
                    else
                        normalized["result"] =
                            new JsonObject();
                    return normalized;
                }
            }
        }

        async Task<JsonObject> ImportStagedBluRayLegacyAsync(
            JsonObject manifest,
            IList<BluRayTransferFile> staged)
        {
            using (var multipart =
                new MultipartFormDataContent())
            {
                var manifestContent = new StringContent(
                    manifest.Stringify(),
                    Encoding.UTF8,
                    "application/json");
                multipart.Add(manifestContent, "manifest");

                foreach (var stagedFile in staged)
                {
                    var content =
                        await StorageFileHttpContent.CreateAsync(
                            stagedFile.File);
                    content.Headers.ContentType =
                        new MediaTypeHeaderValue("video/mp2t");
                    multipart.Add(
                        content,
                        "files",
                        stagedFile.SourceName);
                }

                var ingestBase = BuildLegacyIngestBaseUrl(
                    BaseUrl);
                using (var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(
                        ingestBase +
                        "/api/v1/ingest/bluray-programme")))
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromHours(4);
                    request.Headers.Accept.Add(
                        new MediaTypeWithQualityHeaderValue(
                            "application/json"));
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue(
                            "Bearer",
                            UserToken);
                    request.Content = multipart;

                    var response = await client.SendAsync(request);
                    var body =
                        await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            CoreTransferError(
                                body,
                                "Core Blu-ray ingest failed (" +
                                (int)response.StatusCode + ")."));

                    var parsed = JsonObject.Parse(
                        string.IsNullOrWhiteSpace(body)
                            ? "{}"
                            : body);
                    if (!parsed.ContainsKey("ok") ||
                        parsed["ok"].ValueType !=
                            JsonValueType.Boolean ||
                        !parsed["ok"].GetBoolean())
                        throw new InvalidOperationException(
                            "Core did not confirm the Blu-ray ingest.");

                    return parsed;
                }
            }
        }

        static string CoreTransferError(
            string body,
            string fallback)
        {
            try
            {
                var error = JsonObject.Parse(body);
                return StringValue(
                    error,
                    "detail",
                    StringValue(error, "error", fallback));
            }
            catch
            {
                return fallback;
            }
        }

        static string BuildLegacyIngestBaseUrl(
            string coreBaseUrl)
        {
            var core = new Uri(coreBaseUrl);
            var builder = new UriBuilder(core)
            {
                Port = 8082,
                Path = "",
                Query = "",
                Fragment = ""
            };
            return builder.Uri.ToString().TrimEnd('/');
        }

        sealed class BluRayTransferFile
        {
            public string SourceName;
            public StorageFile File;
        }

        static async Task<string> Sha256Async(StorageFile file)
        {
            var provider =
                HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var hash = provider.CreateHash();

            using (var stream = await file.OpenReadAsync())
            {
                ulong offset = 0;
                while (offset < stream.Size)
                {
                    var remaining = stream.Size - offset;
                    var requested = (uint)Math.Min(
                        1024 * 1024,
                        (long)Math.Min(remaining, (ulong)uint.MaxValue));

                    using (var input = stream.GetInputStreamAt(offset))
                    {
                        var buffer = new Windows.Storage.Streams.Buffer(requested);
                        var read = await input.ReadAsync(
                            buffer,
                            requested,
                            InputStreamOptions.None);

                        if (read.Length == 0)
                            break;

                        hash.Append(read);
                        offset += read.Length;
                    }
                }
            }

            return CryptographicBuffer
                .EncodeToHexString(hash.GetValueAndReset())
                .ToLowerInvariant();
        }

        public async Task<IngestSummary> GetIngestSummaryAsync()
        {
            RequireUserToken();
            var summary = new IngestSummary();

            var capabilities = JsonObject.Parse(await SendAsync(
                HttpMethod.Get, "/api/v1/ingest/capabilities", null, UserToken));
            if (capabilities.ContainsKey("auto_rip") &&
                capabilities["auto_rip"].ValueType == JsonValueType.Boolean)
                summary.AutoRip = capabilities["auto_rip"].GetBoolean();

            var devices = JsonObject.Parse(await SendAsync(
                HttpMethod.Get, "/api/v1/ingest/devices", null, UserToken));
            if (devices.ContainsKey("optical") && devices["optical"].ValueType == JsonValueType.Array)
                summary.OpticalDrives = devices.GetNamedArray("optical").Count;

            var jobs = JsonObject.Parse(await SendAsync(
                HttpMethod.Get, "/api/v1/ingest/jobs", null, UserToken));
            if (jobs.ContainsKey("jobs") && jobs["jobs"].ValueType == JsonValueType.Array)
            {
                foreach (var value in jobs.GetNamedArray("jobs"))
                {
                    if (value.ValueType != JsonValueType.Object) continue;
                    var job = value.GetObject();
                    var state = StringValue(job, "state",
                        StringValue(job, "status", ""));
                    if (state.Equals("queued", StringComparison.OrdinalIgnoreCase) ||
                        state.Equals("processing", StringComparison.OrdinalIgnoreCase) ||
                        state.Equals("running", StringComparison.OrdinalIgnoreCase) ||
                        state.Equals("ripping", StringComparison.OrdinalIgnoreCase))
                    {
                        summary.ActiveJobs++;
                        if (string.IsNullOrWhiteSpace(summary.CurrentJob))
                            summary.CurrentJob = StringValue(job, "label",
                                StringValue(job, "title", "Music disc"));
                    }
                }
            }
            return summary;
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
            request.Headers.UserAgent.ParseAdd("Brimstone-Xbox/0.2.18");

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
