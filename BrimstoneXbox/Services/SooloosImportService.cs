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
    public sealed class SooloosImportService
    {
        readonly string _host;
        readonly HttpClient _http = new HttpClient();
        string _sessionId = "";
        int _requestId = 1;

        public SooloosImportService(string host)
        {
            _host = SooloosClient.NormaliseHost(host);
            _http.Timeout = TimeSpan.FromSeconds(30);
        }

        public async Task<JsonObject> ImportAlbumAsync(CoreAlbum album)
        {
            if (album == null || album.Tracks == null || album.Tracks.Count == 0)
                throw new InvalidOperationException("This album has no Xbox-local tracks to send.");
            if (string.IsNullOrWhiteSpace(_host))
                throw new InvalidOperationException("Enter the Sooloos Core address.");

            var artist = (album.Artist ?? "Unknown Artist").Trim();
            var title = (album.Title ?? "Unknown Album").Trim();

            var broker = new SooloosClient(_host);
            var matches = await broker.SearchAlbumsAsync(title);
            if (matches.Any(a =>
                string.Equals(a.Title, title, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Artist, artist, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    artist + " — " + title + " already exists on the Sooloos Core.");

            var items = new List<ImportItem>();
            var trackNumber = 1;
            foreach (var track in album.Tracks.OrderBy(t => t.Id))
            {
                if (track == null || string.IsNullOrWhiteSpace(track.LocalPath))
                    continue;

                var absolute = System.IO.Path.Combine(
                    ApplicationData.Current.LocalFolder.Path,
                    track.LocalPath);
                var file = await StorageFile.GetFileFromPathAsync(absolute);
                var props = await file.GetBasicPropertiesAsync();
                var encoded = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(file.Path ?? file.Name));

                items.Add(new ImportItem
                {
                    Track = track,
                    Number = trackNumber++,
                    File = file,
                    EncodedPath = encoded,
                    Bytes = props.Size
                });
            }

            if (items.Count == 0)
                throw new InvalidOperationException("No readable Xbox-local files were found.");

            await EnsureSessionAsync();

            var deviceId = RandomDeviceSooid();
            var info = new JsonObject
            {
                ["DeviceId:sooid"] = JsonValue.CreateStringValue(deviceId),
                ["Description"] = JsonValue.CreateStringValue("Xbox Core import"),
                ["Capabilities"] = StringArray("Import"),
                ["ConfigurationType"] = JsonValue.CreateStringValue("Static")
            };
            var aux = new JsonObject
            {
                ["Info"] = info,
                ["PingSeconds"] = JsonValue.CreateNumberValue(20)
            };

            var auxRid = NextRequestId();
            await SendMessageAsync(
                "Sooloos.Msg.AuxDevice.AuxDeviceConnectRequest",
                aux,
                auxRid);
            await WaitForMessageAsync("AuxDeviceConnectResponse", auxRid, 20);

            var musicFiles = new JsonArray();
            foreach (var item in items)
            {
                var fileInfo = new JsonObject
                {
                    ["MediaPath"] = JsonValue.CreateStringValue(item.EncodedPath),
                    ["OriginalPath"] = JsonValue.CreateStringValue(item.File.Path ?? item.File.Name),
                    ["FileSize:long"] = JsonValue.CreateStringValue(item.Bytes.ToString()),
                    ["IsDirectory"] = JsonValue.CreateBooleanValue(false)
                };

                var row = new JsonObject
                {
                    ["FileInfo"] = fileInfo,
                    ["ExtractedTags"] = Tags(
                        artist,
                        title,
                        item.Track.Title ?? ("Track " + item.Number.ToString("00")),
                        item.Number),
                    ["CoverUrls"] = new JsonArray()
                };
                musicFiles.Add(row);
            }

            var create = new JsonObject
            {
                ["ImportDeviceId:sooid"] = JsonValue.CreateStringValue(deviceId),
                ["MusicFiles"] = musicFiles,
                ["CreateOptions"] = StringArray(
                    "AutoSkipDuplicates",
                    "PreferUserMetadata",
                    "Private")
            };

            var createRid = NextRequestId();
            await SendMessageAsync(
                "Sooloos.Msg.Import.CreateLooseFilesMusicProjectRequest",
                create,
                createRid);

            string projectId = null;
            var creationComplete = false;
            var createDeadline = DateTimeOffset.UtcNow.AddSeconds(40);
            var lastPing = DateTimeOffset.UtcNow;

            while (DateTimeOffset.UtcNow < createDeadline &&
                   (string.IsNullOrWhiteSpace(projectId) || !creationComplete))
            {
                if ((DateTimeOffset.UtcNow - lastPing).TotalSeconds >= 5)
                {
                    await PingAsync(deviceId);
                    lastPing = DateTimeOffset.UtcNow;
                }

                var rows = await PollAsync();
                foreach (var value in rows)
                {
                    if (value.ValueType != JsonValueType.Object)
                        continue;
                    var row = value.GetObject();
                    if (RowRequestId(row) != createRid)
                        continue;

                    var created = FindMessage(row, "ProjectCreatedResponse");
                    if (created != null)
                    {
                        projectId = StringValue(created, "ProjectId:guid", projectId ?? "");
                        if (BoolValue(created, "ProjectAlreadyExists"))
                            throw new InvalidOperationException(
                                "Sooloos reports that this import project already exists.");
                    }

                    if (FindMessage(row, "ProjectCreationCompletedResponse") != null)
                        creationComplete = true;
                }
            }

            if (string.IsNullOrWhiteSpace(projectId))
                throw new InvalidOperationException(
                    "Sooloos did not create an import project.");

            var subscribeRid = NextRequestId();
            await SendMessageAsync(
                "Sooloos.Msg.Import.ImportProjectSubscribeRequest",
                new JsonObject
                {
                    ["ProjectId:guid"] = JsonValue.CreateStringValue(projectId)
                },
                subscribeRid);

            var byEncoded = items.ToDictionary(
                x => x.EncodedPath,
                x => x,
                StringComparer.Ordinal);
            var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string mediaId = null;
            var deadline = DateTimeOffset.UtcNow.AddMinutes(4);

            while (DateTimeOffset.UtcNow < deadline)
            {
                if ((DateTimeOffset.UtcNow - lastPing).TotalSeconds >= 5)
                {
                    await PingAsync(deviceId);
                    lastPing = DateTimeOffset.UtcNow;
                }

                var rows = await PollAsync();
                foreach (var value in rows)
                {
                    if (value.ValueType != JsonValueType.Object)
                        continue;
                    var row = value.GetObject();

                    var copy = FindMessage(row, "MediaCopyCommand");
                    if (copy != null)
                        await HandleCopyAsync(
                            copy,
                            projectId,
                            deviceId,
                            byEncoded,
                            album);

                    var subscribed = FindMessage(
                        row,
                        "ImportProjectSubscribeResponse");
                    if (subscribed != null)
                        await ApplyMediaChangesAsync(
                            MediaChangesFromSubscription(subscribed),
                            projectId,
                            approved,
                            completed,
                            id => mediaId = mediaId ?? id);

                    var updated = FindMessage(
                        row,
                        "ImportProjectUpdatedResponse");
                    if (updated != null)
                        await ApplyMediaChangesAsync(
                            MediaChangesFromUpdate(updated),
                            projectId,
                            approved,
                            completed,
                            id => mediaId = mediaId ?? id);
                }

                if (!string.IsNullOrWhiteSpace(mediaId) && completed.Count > 0)
                {
                    var verify = await broker.SearchAlbumsAsync(title);
                    var visible = verify.Any(a =>
                        string.Equals(a.Title, title, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(a.Artist, artist, StringComparison.OrdinalIgnoreCase));

                    return new JsonObject
                    {
                        ["ok"] = JsonValue.CreateBooleanValue(true),
                        ["album"] = JsonValue.CreateStringValue(title),
                        ["artist"] = JsonValue.CreateStringValue(artist),
                        ["media_id"] = JsonValue.CreateStringValue(mediaId),
                        ["track_count"] = JsonValue.CreateNumberValue(items.Count),
                        ["verified"] = JsonValue.CreateBooleanValue(visible)
                    };
                }
            }

            throw new InvalidOperationException(
                "Sooloos import timed out before the Core confirmed completion.");
        }

        async Task ApplyMediaChangesAsync(
            IEnumerable<MediaChange> changes,
            string projectId,
            HashSet<string> approved,
            HashSet<string> completed,
            Action<string> observeMediaId)
        {
            foreach (var change in changes)
            {
                if (change.Duplicate)
                    throw new InvalidOperationException(
                        "Sooloos detected an existing copy; nothing was overwritten.");

                if (!string.IsNullOrWhiteSpace(change.MediaId))
                    observeMediaId(change.MediaId);

                var state = (change.Status ?? "").ToLowerInvariant();
                if (state == "waitingforapproval" &&
                    !string.IsNullOrWhiteSpace(change.MediaId) &&
                    !approved.Contains(change.MediaId))
                {
                    await ApproveAsync(projectId, change.MediaId);
                    approved.Add(change.MediaId);
                }
                else if (state == "complete" &&
                         !string.IsNullOrWhiteSpace(change.MediaId))
                {
                    completed.Add(change.MediaId);
                }
                else if (state == "failed")
                {
                    throw new InvalidOperationException(
                        "Sooloos reported that the import failed.");
                }
            }
        }

        async Task ApproveAsync(string projectId, string mediaId)
        {
            await SendMessageAsync(
                "Sooloos.Msg.Import.MusicProjectReapplyUserMetadataRequest",
                new JsonObject
                {
                    ["ProjectId:guid"] = JsonValue.CreateStringValue(projectId),
                    ["AlbumId:sooid"] = JsonValue.CreateStringValue(mediaId)
                },
                NextRequestId());

            await Task.Delay(500);

            await SendMessageAsync(
                "Sooloos.Msg.Import.ImportProjectApproveRequest",
                new JsonObject
                {
                    ["ProjectId:guid"] = JsonValue.CreateStringValue(projectId),
                    ["MediaId:sooid"] = JsonValue.CreateStringValue(mediaId),
                    ["ClobberDuplicatesByName"] =
                        JsonValue.CreateBooleanValue(false)
                },
                NextRequestId());
        }

        async Task HandleCopyAsync(
            JsonObject copy,
            string projectId,
            string deviceId,
            Dictionary<string, ImportItem> items,
            CoreAlbum album)
        {
            if (!string.Equals(
                StringValue(copy, "ProjectId:guid", ""),
                projectId,
                StringComparison.OrdinalIgnoreCase))
                return;

            var copyId = StringValue(copy, "CopyId:guid", "");
            if (string.IsNullOrWhiteSpace(copyId) ||
                !copy.ContainsKey("Media") ||
                copy["Media"].ValueType != JsonValueType.Array)
                throw new InvalidOperationException(
                    "Sooloos returned an unexpected media-copy request.");

            foreach (var value in copy.GetNamedArray("Media"))
            {
                if (value.ValueType != JsonValueType.Object)
                    continue;
                var target = value.GetObject();
                var encoded = StringValue(target, "MediaPath", "");
                var targetUrl = StringValue(target, "TargetUrl", "");

                ImportItem item;
                Uri uri;
                if (!items.TryGetValue(encoded, out item) ||
                    !Uri.TryCreate(targetUrl, UriKind.Absolute, out uri) ||
                    !string.Equals(
                        uri.Authority,
                        _host,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Sooloos requested a transfer target that was not staged.");

                var sourceSha = await Sha256Async(item.File);

                using (var source = await item.File.OpenStreamForReadAsync())
                using (var request = new HttpRequestMessage(HttpMethod.Put, uri))
                using (var content = new StreamContent(source))
                {
                    content.Headers.ContentType =
                        new MediaTypeHeaderValue("application/octet-stream");
                    content.Headers.ContentLength = (long)item.Bytes;
                    request.Content = content;

                    var response = await _http.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            "Sooloos rejected the audio upload.");
                }

                using (var response = await _http.GetAsync(uri))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            "Could not verify the audio stored by Sooloos.");

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    {
                        var storedSha = await Sha256Async(stream);
                        if (!string.Equals(
                            sourceSha,
                            storedSha,
                            StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(
                                "Sooloos stored audio checksum does not match the Xbox source.");
                    }
                }
            }

            var extra = FindMessage(copy, "AlbumCopyExtraInfo");
            if (extra != null)
            {
                var albumId = StringValue(extra, "AlbumId:sooid", "");
                if (!string.IsNullOrWhiteSpace(albumId))
                    await SendProjectExtraAsync(
                        projectId,
                        deviceId,
                        albumId,
                        album);
            }

            var broadcast = new JsonObject
            {
                ["DeviceId:sooid"] = JsonValue.CreateStringValue(deviceId),
                ["Message:Sooloos.Msg.ImportDevice.MediaCopyStatusBroadcast"] =
                    new JsonObject
                    {
                        ["CopyId:guid"] = JsonValue.CreateStringValue(copyId),
                        ["CopyPercent"] = JsonValue.CreateNumberValue(100)
                    }
            };

            await SendMessageAsync(
                "Sooloos.Msg.AuxDevice.AuxDeviceBroadcastRequest",
                broadcast,
                NextRequestId());
        }

        async Task SendProjectExtraAsync(
            string projectId,
            string deviceId,
            string albumId,
            CoreAlbum album)
        {
            var tracks = new JsonArray();
            var number = 1;
            foreach (var track in album.Tracks)
            {
                var durationMs = Math.Max(
                    0,
                    (int)Math.Round(track.DurationSeconds * 1000.0));

                tracks.Add(new JsonObject
                {
                    ["TrackNumber"] = JsonValue.CreateNumberValue(number++),
                    ["TrackGain:real"] = JsonValue.CreateStringValue("0"),
                    ["TrackPeak:real"] = JsonValue.CreateStringValue("1"),
                    ["RealStartMs"] = JsonValue.CreateNumberValue(0),
                    ["RealEndMs"] = JsonValue.CreateNumberValue(durationMs),
                    ["NoiseStartMs"] = JsonValue.CreateNumberValue(0),
                    ["NoiseEndMs"] = JsonValue.CreateNumberValue(durationMs)
                });
            }

            var albums = new JsonArray();
            albums.Add(new JsonObject
            {
                ["AlbumId:sooid"] = JsonValue.CreateStringValue(albumId),
                ["AlbumGain:real"] = JsonValue.CreateStringValue("0"),
                ["AlbumPeak:real"] = JsonValue.CreateStringValue("1"),
                ["Tracks"] = tracks
            });

            var extra = new JsonObject
            {
                ["Albums"] = albums
            };

            var payload = new JsonObject
            {
                ["DeviceId:sooid"] = JsonValue.CreateStringValue(deviceId),
                ["Message:Sooloos.Msg.ImportDevice.ProjectExtraInfoBroadcast"] =
                    new JsonObject
                    {
                        ["ProjectId:guid"] =
                            JsonValue.CreateStringValue(projectId),
                        ["ExtraInfo:Sooloos.Msg.ImportDevice.LooseFilesExtraInfo"] =
                            extra
                    }
            };

            await SendMessageAsync(
                "Sooloos.Msg.AuxDevice.AuxDeviceBroadcastRequest",
                payload,
                NextRequestId());
        }

        async Task EnsureSessionAsync()
        {
            if (!string.IsNullOrWhiteSpace(_sessionId))
                return;

            var response = await PostAsync(
                "/message/session/newsession",
                null,
                "");
            IEnumerable<string> values;
            if (!response.Headers.TryGetValues("Message-Session", out values))
                response.Headers.TryGetValues("message-session", out values);
            _sessionId = values == null
                ? ""
                : values.FirstOrDefault() ?? "";

            if (!response.IsSuccessStatusCode ||
                string.IsNullOrWhiteSpace(_sessionId))
                throw new InvalidOperationException(
                    "Sooloos Core did not create an import session.");
        }

        async Task PingAsync(string deviceId)
        {
            try
            {
                await SendMessageAsync(
                    "Sooloos.Msg.AuxDevice.AuxDevicePingRequest",
                    new JsonObject
                    {
                        ["DeviceId:sooid"] =
                            JsonValue.CreateStringValue(deviceId)
                    },
                    0);
            }
            catch
            {
                // The next poll/copy operation will surface a lost session.
            }
        }

        async Task WaitForMessageAsync(
            string suffix,
            int requestId,
            int timeoutSeconds)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var rows = await PollAsync();
                foreach (var value in rows)
                {
                    if (value.ValueType != JsonValueType.Object)
                        continue;
                    var row = value.GetObject();
                    if (RowRequestId(row) == requestId &&
                        FindMessage(row, suffix) != null)
                        return;
                }
            }

            throw new InvalidOperationException(
                "Timed out waiting for " + suffix + ".");
        }

        async Task SendMessageAsync(
            string type,
            JsonObject fields,
            int requestId)
        {
            await EnsureSessionAsync();
            var row = new JsonObject
            {
                ["message:" + type] = fields ?? new JsonObject(),
                ["requestid"] = JsonValue.CreateNumberValue(requestId)
            };
            var body = new JsonArray();
            body.Add(row);

            var response = await PostAsync(
                "/message/session/sendrequests",
                body.Stringify(),
                _sessionId);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    "Sooloos rejected the import request.");
        }

        async Task<JsonArray> PollAsync()
        {
            var response = await PostAsync(
                "/message/session/getresponses",
                null,
                _sessionId);

            if ((int)response.StatusCode == 204)
                return new JsonArray();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    "Sooloos import response failed.");

            var body = await response.Content.ReadAsStringAsync();
            JsonArray rows;
            return JsonArray.TryParse(body, out rows)
                ? rows
                : new JsonArray();
        }

        async Task<HttpResponseMessage> PostAsync(
            string path,
            string json,
            string session)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri("http://" + _host + path));
            if (!string.IsNullOrWhiteSpace(session))
                request.Headers.TryAddWithoutValidation(
                    "Message-Session",
                    session);
            if (json != null)
                request.Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json");
            else
                request.Content = new ByteArrayContent(new byte[0]);
            return await _http.SendAsync(request);
        }

        static JsonObject FindMessage(JsonObject obj, string suffix)
        {
            if (obj == null)
                return null;

            foreach (var pair in obj)
            {
                if ((pair.Key.StartsWith("message:", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.StartsWith("Message:", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.StartsWith("ExtraInfo:", StringComparison.OrdinalIgnoreCase)) &&
                    pair.Key.EndsWith(suffix, StringComparison.Ordinal) &&
                    pair.Value.ValueType == JsonValueType.Object)
                    return pair.Value.GetObject();

                if (pair.Value.ValueType == JsonValueType.Object)
                {
                    var nested = FindMessage(pair.Value.GetObject(), suffix);
                    if (nested != null) return nested;
                }
                else if (pair.Value.ValueType == JsonValueType.Array)
                {
                    var nested = FindMessage(pair.Value.GetArray(), suffix);
                    if (nested != null) return nested;
                }
            }

            return null;
        }

        static JsonObject FindMessage(JsonArray array, string suffix)
        {
            if (array == null)
                return null;
            foreach (var value in array)
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var found = FindMessage(value.GetObject(), suffix);
                    if (found != null) return found;
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var found = FindMessage(value.GetArray(), suffix);
                    if (found != null) return found;
                }
            }
            return null;
        }

        static IEnumerable<MediaChange> MediaChangesFromSubscription(
            JsonObject subscribed)
        {
            var result = new List<MediaChange>();
            if (subscribed == null ||
                !subscribed.ContainsKey("ProjectData") ||
                subscribed["ProjectData"].ValueType != JsonValueType.Object)
                return result;

            var project = subscribed.GetNamedObject("ProjectData");
            if (!project.ContainsKey("Media") ||
                project["Media"].ValueType != JsonValueType.Array)
                return result;

            foreach (var value in project.GetNamedArray("Media"))
            {
                if (value.ValueType != JsonValueType.Object)
                    continue;
                var row = value.GetObject();
                var package = row.ContainsKey("MetadataPackage") &&
                              row["MetadataPackage"].ValueType == JsonValueType.Object
                    ? row.GetNamedObject("MetadataPackage")
                    : null;

                result.Add(new MediaChange
                {
                    MediaId = package == null
                        ? ""
                        : StringValue(package, "MediaId:sooid", ""),
                    Status = StringValue(row, "Status", ""),
                    Duplicate = BoolValue(row, "IsDuplicate")
                });
            }

            return result;
        }

        static IEnumerable<MediaChange> MediaChangesFromUpdate(
            JsonObject update)
        {
            var result = new List<MediaChange>();
            if (update == null)
                return result;

            if (update.ContainsKey("MediaAdded") &&
                update["MediaAdded"].ValueType == JsonValueType.Array)
            {
                foreach (var value in update.GetNamedArray("MediaAdded"))
                {
                    if (value.ValueType != JsonValueType.Object) continue;
                    var row = value.GetObject();
                    var package = row.ContainsKey("MetadataPackage") &&
                                  row["MetadataPackage"].ValueType == JsonValueType.Object
                        ? row.GetNamedObject("MetadataPackage")
                        : null;
                    result.Add(new MediaChange
                    {
                        MediaId = package == null
                            ? ""
                            : StringValue(package, "MediaId:sooid", ""),
                        Status = StringValue(row, "Status", ""),
                        Duplicate = BoolValue(row, "IsDuplicate")
                    });
                }
            }

            if (update.ContainsKey("MediaStatusUpdated") &&
                update["MediaStatusUpdated"].ValueType == JsonValueType.Array)
            {
                foreach (var value in update.GetNamedArray("MediaStatusUpdated"))
                {
                    if (value.ValueType != JsonValueType.Object) continue;
                    var row = value.GetObject();
                    result.Add(new MediaChange
                    {
                        MediaId = StringValue(row, "MediaId:sooid", ""),
                        Status = StringValue(row, "Status", ""),
                        Duplicate = BoolValue(row, "IsDuplicate")
                    });
                }
            }

            return result;
        }

        static JsonArray Tags(
            string artist,
            string album,
            string title,
            int trackNumber)
        {
            var tags = new JsonArray();
            AddTag(tags, "ARTIST", artist);
            AddTag(tags, "ALBUMARTIST", artist);
            AddTag(tags, "ALBUM", album);
            AddTag(tags, "TITLE", title);
            AddTag(tags, "TRACKNUMBER", trackNumber.ToString());
            return tags;
        }

        static void AddTag(JsonArray tags, string name, string value)
        {
            tags.Add(new JsonObject
            {
                ["Name"] = JsonValue.CreateStringValue(name ?? ""),
                ["Value"] = JsonValue.CreateStringValue(value ?? "")
            });
        }

        static JsonArray StringArray(params string[] values)
        {
            var result = new JsonArray();
            foreach (var value in values ?? new string[0])
                result.Add(JsonValue.CreateStringValue(value ?? ""));
            return result;
        }

        static string RandomDeviceSooid()
        {
            var bytes = Guid.NewGuid().ToByteArray();
            return "1801" +
                string.Concat(bytes.Select(b => b.ToString("x2")));
        }

        int NextRequestId() => _requestId++;

        static int RowRequestId(JsonObject row)
        {
            return row != null &&
                   row.ContainsKey("requestid") &&
                   row["requestid"].ValueType == JsonValueType.Number
                ? (int)row["requestid"].GetNumber()
                : -1;
        }

        static string StringValue(
            JsonObject obj,
            string key,
            string fallback = "")
        {
            if (obj == null ||
                !obj.ContainsKey(key))
                return fallback;
            var value = obj[key];
            if (value.ValueType == JsonValueType.String)
                return value.GetString();
            if (value.ValueType == JsonValueType.Number)
                return value.GetNumber().ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            return fallback;
        }

        static bool BoolValue(JsonObject obj, string key)
        {
            return obj != null &&
                   obj.ContainsKey(key) &&
                   obj[key].ValueType == JsonValueType.Boolean &&
                   obj[key].GetBoolean();
        }

        static async Task<string> Sha256Async(StorageFile file)
        {
            using (var stream = await file.OpenReadAsync())
                return await Sha256Async(stream);
        }

        static async Task<string> Sha256Async(IRandomAccessStream stream)
        {
            var provider =
                HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var hash = provider.CreateHash();
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
                    if (read.Length == 0) break;
                    hash.Append(read);
                    offset += read.Length;
                }
            }

            return CryptographicBuffer
                .EncodeToHexString(hash.GetValueAndReset())
                .ToLowerInvariant();
        }

        static async Task<string> Sha256Async(System.IO.Stream stream)
        {
            var provider =
                HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var hash = provider.CreateHash();
            var buffer = new byte[1024 * 1024];

            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer,
                    0,
                    buffer.Length);
                if (read <= 0) break;
                hash.Append(CryptographicBuffer.CreateFromByteArray(
                    buffer.Take(read).ToArray()));
            }

            return CryptographicBuffer
                .EncodeToHexString(hash.GetValueAndReset())
                .ToLowerInvariant();
        }

        sealed class ImportItem
        {
            public CoreTrack Track;
            public int Number;
            public StorageFile File;
            public string EncodedPath;
            public ulong Bytes;
        }

        sealed class MediaChange
        {
            public string MediaId;
            public string Status;
            public bool Duplicate;
        }
    }
}
