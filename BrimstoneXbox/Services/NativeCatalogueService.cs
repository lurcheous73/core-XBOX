using BrimstoneXbox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.UI.Xaml.Media.Imaging;

namespace BrimstoneXbox.Services
{
    public sealed class NativeCatalogueService
    {
        readonly DiscMetadataService _metadata = new DiscMetadataService();
        public async Task<List<CoreAlbum>> GetAlbumsAsync()
        {
            var result = new List<CoreAlbum>();
            var root = ApplicationData.Current.LocalFolder;
            var core = await root.CreateFolderAsync("Core", CreationCollisionOption.OpenIfExists);
            var ingest = await core.CreateFolderAsync("Ingest", CreationCollisionOption.OpenIfExists);
            var folders = await ingest.GetFoldersAsync();

            long idBase = 1000000;
            foreach (var folder in folders)
            {
                if (!folder.Name.StartsWith("CD-", StringComparison.OrdinalIgnoreCase))
                    continue;

                StorageFile statusFile;
                try
                {
                    statusFile = await folder.GetFileAsync("rip-status.json");
                }
                catch
                {
                    continue;
                }

                JsonObject status;
                try
                {
                    var text = await FileIO.ReadTextAsync(statusFile);
                    if (!JsonObject.TryParse(text, out status))
                        continue;
                }
                catch
                {
                    continue;
                }

                if (!string.Equals(StringValue(status, "state"), "complete",
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                JsonObject metadata = null;
                try
                {
                    var metadataFile = await folder.GetFileAsync("metadata.json");
                    JsonObject parsed;
                    if (JsonObject.TryParse(await FileIO.ReadTextAsync(metadataFile), out parsed))
                        metadata = parsed;
                }
                catch
                {
                    // Existing anonymous rips are upgraded in-place on the next scan.
                    metadata = await _metadata.EnrichFromRipStatusAsync(folder, status);
                }

                if (metadata == null)
                    metadata = await _metadata.EnrichFromRipStatusAsync(folder, status);

                var fingerprint = StringValue(status, "fingerprint", folder.Name);
                var title = metadata == null
                    ? "Audio CD " + ShortFingerprint(fingerprint)
                    : StringValue(metadata, "title", "Audio CD " + ShortFingerprint(fingerprint));
                var artist = metadata == null
                    ? "Unknown Artist"
                    : StringValue(metadata, "artist", "Unknown Artist");

                var album = new CoreAlbum
                {
                    Id = fingerprint,
                    Title = title,
                    Artist = artist
                };

                try
                {
                    await folder.GetFileAsync("cover.jpg");
                    album.Artwork = new BitmapImage(new Uri(
                        "ms-appdata:///local/Core/Ingest/" +
                        folder.Name +
                        "/cover.jpg"));
                }
                catch
                {
                    album.Artwork = null;
                }

                if (status.ContainsKey("tracks") &&
                    status["tracks"].ValueType == JsonValueType.Array)
                {
                    var trackMetadata = metadata != null &&
                                        metadata.ContainsKey("tracks") &&
                                        metadata["tracks"].ValueType == JsonValueType.Array
                        ? metadata.GetNamedArray("tracks")
                        : null;

                    foreach (var value in status.GetNamedArray("tracks"))
                    {
                        if (value.ValueType != JsonValueType.Object)
                            continue;

                        var row = value.GetObject();
                        var number = (int)NumberValue(row, "track", album.Tracks.Count + 1);
                        var file = StringValue(row, "file", "Track " + number.ToString("00") + ".wav");
                        var trackTitle = "Track " + number.ToString("00");

                        if (trackMetadata != null && number > 0 && number <= trackMetadata.Count)
                        {
                            var metaValue = trackMetadata[number - 1];
                            if (metaValue.ValueType == JsonValueType.String)
                                trackTitle = metaValue.GetString();
                            else if (metaValue.ValueType == JsonValueType.Object)
                                trackTitle = StringValue(metaValue.GetObject(), "title", trackTitle);
                        }

                        album.Tracks.Add(new CoreTrack
                        {
                            Id = idBase++,
                            Title = trackTitle,
                            Artist = artist,
                            Album = title,
                            DurationSeconds = NumberValue(row, "audio_seconds", 0),
                            LocalPath = Path.Combine(
                                "Core", "Ingest", folder.Name, file)
                        });
                    }
                }

                result.Add(album);
            }

            await PersistDatabaseAsync(result);
            return result;
        }

        async Task PersistDatabaseAsync(List<CoreAlbum> albums)
        {
            try
            {
                var root = ApplicationData.Current.LocalFolder;
                var core = await root.CreateFolderAsync("Core", CreationCollisionOption.OpenIfExists);
                var catalogue = await core.CreateFolderAsync("Catalogue", CreationCollisionOption.OpenIfExists);
                var file = await catalogue.CreateFileAsync(
                    "core-database.json",
                    CreationCollisionOption.OpenIfExists);

                JsonObject database = null;
                try
                {
                    var existing = await FileIO.ReadTextAsync(file);
                    JsonObject parsed;
                    if (JsonObject.TryParse(existing, out parsed))
                        database = parsed;
                }
                catch { }

                if (database == null)
                    database = new JsonObject();

                database["schema"] = JsonValue.CreateNumberValue(1);
                database["backend"] = JsonValue.CreateStringValue("xbox-embedded");
                database["updated"] = JsonValue.CreateStringValue(DateTimeOffset.UtcNow.ToString("o"));

                var albumRows = new JsonArray();
                var trackRows = new JsonArray();

                foreach (var album in albums)
                {
                    albumRows.Add(new JsonObject
                    {
                        ["id"] = JsonValue.CreateStringValue(album.Id ?? ""),
                        ["title"] = JsonValue.CreateStringValue(album.Title ?? ""),
                        ["artist"] = JsonValue.CreateStringValue(album.Artist ?? ""),
                        ["track_count"] = JsonValue.CreateNumberValue(album.Tracks.Count)
                    });

                    var position = 1;
                    foreach (var track in album.Tracks)
                    {
                        trackRows.Add(new JsonObject
                        {
                            ["id"] = JsonValue.CreateNumberValue(track.Id),
                            ["album_id"] = JsonValue.CreateStringValue(album.Id ?? ""),
                            ["position"] = JsonValue.CreateNumberValue(position++),
                            ["title"] = JsonValue.CreateStringValue(track.Title ?? ""),
                            ["artist"] = JsonValue.CreateStringValue(track.Artist ?? ""),
                            ["album"] = JsonValue.CreateStringValue(track.Album ?? ""),
                            ["duration_seconds"] = JsonValue.CreateNumberValue(track.DurationSeconds),
                            ["local_path"] = JsonValue.CreateStringValue(track.LocalPath ?? "")
                        });
                    }
                }

                database["albums"] = albumRows;
                database["tracks"] = trackRows;
                if (!database.ContainsKey("sources"))
                    database["sources"] = new JsonArray();
                if (!database.ContainsKey("users"))
                    database["users"] = new JsonArray();

                await FileIO.WriteTextAsync(file, database.Stringify());
            }
            catch
            {
                // A failed diagnostic/index write must not hide playable media.
            }
        }

        public async Task<JsonObject> BuildAlbumsApiAsync()
        {
            var albums = await GetAlbumsAsync();
            var rows = new JsonArray();

            foreach (var album in albums)
            {
                var tracks = new JsonArray();
                foreach (var track in album.Tracks)
                {
                    var metadata = new JsonObject
                    {
                        ["title"] = JsonValue.CreateStringValue(track.Title ?? ""),
                        ["artist"] = JsonValue.CreateStringValue(track.Artist ?? ""),
                        ["album"] = JsonValue.CreateStringValue(track.Album ?? ""),
                        ["duration_seconds"] = JsonValue.CreateNumberValue(track.DurationSeconds)
                    };

                    tracks.Add(new JsonObject
                    {
                        ["id"] = JsonValue.CreateNumberValue(track.Id),
                        ["title"] = JsonValue.CreateStringValue(track.Title ?? ""),
                        ["artist"] = JsonValue.CreateStringValue(track.Artist ?? ""),
                        ["duration_seconds"] = JsonValue.CreateNumberValue(track.DurationSeconds),
                        ["metadata"] = metadata
                    });
                }

                var edition = new JsonObject
                {
                    ["id"] = JsonValue.CreateStringValue((album.Id ?? "") + ":xbox"),
                    ["title"] = JsonValue.CreateStringValue(album.Title ?? ""),
                    ["media_type"] = JsonValue.CreateStringValue("cd"),
                    ["source_format"] = JsonValue.CreateStringValue("wav"),
                    ["tracks"] = tracks
                };
                var editions = new JsonArray();
                editions.Add(edition);

                rows.Add(new JsonObject
                {
                    ["id"] = JsonValue.CreateStringValue(album.Id ?? ""),
                    ["title"] = JsonValue.CreateStringValue(album.Title ?? ""),
                    ["artist"] = JsonValue.CreateStringValue(album.Artist ?? ""),
                    ["editions"] = editions
                });
            }

            return new JsonObject
            {
                ["albums"] = rows,
                ["count"] = JsonValue.CreateNumberValue(rows.Count)
            };
        }

        public async Task<CoreTrack> FindTrackAsync(long id)
        {
            var albums = await GetAlbumsAsync();
            foreach (var album in albums)
                foreach (var track in album.Tracks)
                    if (track.Id == id)
                        return track;
            return null;
        }

        public async Task PlayTrackIdAsync(long id, double volume = 0.70)
        {
            var track = await FindTrackAsync(id);
            if (track == null)
                throw new InvalidOperationException("Xbox Core track was not found: " + id);
            await PlayTrackAsync(track, volume);
        }

        public async Task PlayProgrammeIdsAsync(
            IList<long> ids,
            double volume = 0.70)
        {
            if (ids == null || ids.Count == 0)
                throw new InvalidOperationException("No Xbox Core tracks were supplied.");

            var albums = await GetAlbumsAsync();
            var byId = new Dictionary<long, CoreTrack>();
            foreach (var album in albums)
                foreach (var track in album.Tracks)
                    byId[track.Id] = track;

            var paths = new List<string>();
            var sources = new JsonArray();
            foreach (var id in ids)
            {
                CoreTrack track;
                if (!byId.TryGetValue(id, out track))
                    continue;

                paths.Add(track.LocalPath);
                sources.Add(TrackMetadata(track));
            }

            if (paths.Count == 0)
                throw new InvalidOperationException("None of the requested Xbox Core tracks were found.");

            await PlaybackService.Instance.PlayLocalProgrammeAsync(
                paths, sources, volume, 0);
        }

        public async Task PlayTrackAsync(CoreTrack track, double volume = 0.70)
        {
            if (track == null || string.IsNullOrWhiteSpace(track.LocalPath))
                throw new InvalidOperationException("Track is not stored on this Xbox.");

            var source = TrackMetadata(track);
            await PlaybackService.Instance.PlayLocalFileAsync(
                track.LocalPath, source, volume, 0);
        }

        public async Task PlayAlbumAsync(CoreAlbum album, double volume = 0.70)
        {
            if (album == null || album.Tracks.Count == 0)
                throw new InvalidOperationException("Album has no local tracks.");

            var paths = new List<string>();
            var sources = new JsonArray();

            foreach (var track in album.Tracks)
            {
                if (string.IsNullOrWhiteSpace(track.LocalPath))
                    continue;
                paths.Add(track.LocalPath);
                sources.Add(TrackMetadata(track));
            }

            if (paths.Count == 0)
                throw new InvalidOperationException("Album has no Xbox-local media.");

            await PlaybackService.Instance.PlayLocalProgrammeAsync(
                paths, sources, volume, 0);
        }

        static JsonObject TrackMetadata(CoreTrack track)
        {
            return new JsonObject
            {
                ["title"] = JsonValue.CreateStringValue(track.Title ?? ""),
                ["artist"] = JsonValue.CreateStringValue(track.Artist ?? ""),
                ["album"] = JsonValue.CreateStringValue(track.Album ?? "")
            };
        }

        static string ShortFingerprint(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint))
                return "Unknown";
            return fingerprint.Length <= 8
                ? fingerprint
                : fingerprint.Substring(0, 8);
        }

        static string StringValue(JsonObject obj, string key, string fallback = "")
        {
            if (obj == null || !obj.ContainsKey(key) ||
                obj[key].ValueType != JsonValueType.String)
                return fallback;
            return obj[key].GetString();
        }

        static double NumberValue(JsonObject obj, string key, double fallback)
        {
            if (obj == null || !obj.ContainsKey(key) ||
                obj[key].ValueType != JsonValueType.Number)
                return fallback;
            return obj[key].GetNumber();
        }
    }
}
