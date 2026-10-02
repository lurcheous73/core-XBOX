using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;

namespace BrimstoneXbox.Services
{
    public sealed class DiscMetadataService
    {
        static readonly SemaphoreSlim MusicBrainzGate = new SemaphoreSlim(1, 1);
        static DateTimeOffset _lastMusicBrainzRequest = DateTimeOffset.MinValue;

        readonly HttpClient _http = new HttpClient();

        public DiscMetadataService()
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "BrimstoneCoreXbox/0.2.10 (core-audio.uk)");
        }

        public async Task<JsonObject> EnrichFromRipStatusAsync(
            StorageFolder discFolder,
            JsonObject ripStatus)
        {
            if (discFolder == null || ripStatus == null)
                return null;

            try
            {
                var existing = await TryReadMetadataAsync(discFolder);
                if (existing != null)
                {
                    await EnsureArtworkAsync(discFolder, existing);
                    return existing;
                }

                var first = (int)Number(ripStatus, "first_track", 1);
                var last = (int)Number(ripStatus, "last_track", 0);
                if (last < first)
                    return null;

                if (!ripStatus.ContainsKey("tracks") ||
                    ripStatus["tracks"].ValueType != JsonValueType.Array)
                    return null;

                var trackRows = ripStatus.GetNamedArray("tracks");
                var offsets = new Dictionary<int, long>();
                long leadoutLba = -1;

                foreach (var value in trackRows)
                {
                    if (value.ValueType != JsonValueType.Object)
                        continue;

                    var track = value.GetObject();
                    var number = (int)Number(track, "track", 0);
                    var start = (long)Number(track, "start_lba", -1);
                    var end = (long)Number(track, "end_lba", -1);

                    if (number > 0 && start >= 0)
                        offsets[number] = start;
                    if (end > leadoutLba)
                        leadoutLba = end;
                }

                if (leadoutLba < 0 || offsets.Count == 0)
                    return null;

                var discId = BuildMusicBrainzDiscId(
                    first, last, leadoutLba, offsets);

                var lookup = await LookupDiscAsync(discId);
                if (lookup == null)
                    return null;

                lookup["disc_id"] = JsonValue.CreateStringValue(discId);
                lookup["source"] = JsonValue.CreateStringValue("MusicBrainz");
                lookup["lookup_at"] = JsonValue.CreateStringValue(
                    DateTimeOffset.UtcNow.ToString("o"));

                var metadataFile = await discFolder.CreateFileAsync(
                    "metadata.json",
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(metadataFile, lookup.Stringify());

                await EnsureArtworkAsync(discFolder, lookup);
                return lookup;
            }
            catch
            {
                return null;
            }
        }

        async Task<JsonObject> LookupDiscAsync(string discId)
        {
            await MusicBrainzGate.WaitAsync();
            try
            {
                var since = DateTimeOffset.UtcNow - _lastMusicBrainzRequest;
                if (since.TotalMilliseconds < 1100)
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(1100 - since.TotalMilliseconds));

                var url =
                    "https://musicbrainz.org/ws/2/discid/" +
                    Uri.EscapeDataString(discId) +
                    "?inc=recordings+artists+release-groups&fmt=json";

                var json = await _http.GetStringAsync(url);
                _lastMusicBrainzRequest = DateTimeOffset.UtcNow;

                JsonObject root;
                if (!JsonObject.TryParse(json, out root) ||
                    !root.ContainsKey("releases") ||
                    root["releases"].ValueType != JsonValueType.Array)
                    return null;

                foreach (var releaseValue in root.GetNamedArray("releases"))
                {
                    if (releaseValue.ValueType != JsonValueType.Object)
                        continue;

                    var release = releaseValue.GetObject();
                    var medium = FindMatchingMedium(release, discId);
                    if (medium == null)
                        continue;

                    var result = BuildMetadata(release, medium);
                    if (result != null)
                        return result;
                }

                return null;
            }
            finally
            {
                MusicBrainzGate.Release();
            }
        }

        static JsonObject FindMatchingMedium(JsonObject release, string discId)
        {
            if (release == null ||
                !release.ContainsKey("media") ||
                release["media"].ValueType != JsonValueType.Array)
                return null;

            JsonObject fallback = null;
            foreach (var mediumValue in release.GetNamedArray("media"))
            {
                if (mediumValue.ValueType != JsonValueType.Object)
                    continue;

                var medium = mediumValue.GetObject();
                if (fallback == null)
                    fallback = medium;

                if (!medium.ContainsKey("discs") ||
                    medium["discs"].ValueType != JsonValueType.Array)
                    continue;

                foreach (var discValue in medium.GetNamedArray("discs"))
                {
                    if (discValue.ValueType != JsonValueType.Object)
                        continue;

                    var disc = discValue.GetObject();
                    if (string.Equals(
                        StringValue(disc, "id"),
                        discId,
                        StringComparison.Ordinal))
                        return medium;
                }
            }

            return fallback;
        }

        static JsonObject BuildMetadata(JsonObject release, JsonObject medium)
        {
            var title = StringValue(release, "title");
            var releaseId = StringValue(release, "id");
            if (string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(releaseId))
                return null;

            var artist = ArtistCredit(release);
            var tracks = new JsonArray();

            if (medium.ContainsKey("tracks") &&
                medium["tracks"].ValueType == JsonValueType.Array)
            {
                foreach (var trackValue in medium.GetNamedArray("tracks"))
                {
                    if (trackValue.ValueType != JsonValueType.Object)
                        continue;

                    var track = trackValue.GetObject();
                    var trackTitle = StringValue(track, "title");
                    var recordingId = "";

                    if (track.ContainsKey("recording") &&
                        track["recording"].ValueType == JsonValueType.Object)
                    {
                        var recording = track.GetNamedObject("recording");
                        if (string.IsNullOrWhiteSpace(trackTitle))
                            trackTitle = StringValue(recording, "title");
                        recordingId = StringValue(recording, "id");
                    }

                    tracks.Add(new JsonObject
                    {
                        ["position"] = JsonValue.CreateNumberValue(
                            Number(track, "position", tracks.Count + 1)),
                        ["title"] = JsonValue.CreateStringValue(
                            string.IsNullOrWhiteSpace(trackTitle)
                                ? "Track " + (tracks.Count + 1).ToString("00")
                                : trackTitle),
                        ["recording_id"] = JsonValue.CreateStringValue(
                            recordingId ?? "")
                    });
                }
            }

            var frontCover = false;
            if (release.ContainsKey("cover-art-archive") &&
                release["cover-art-archive"].ValueType == JsonValueType.Object)
            {
                var cover = release.GetNamedObject("cover-art-archive");
                frontCover = BoolValue(cover, "front");
            }

            return new JsonObject
            {
                ["release_id"] = JsonValue.CreateStringValue(releaseId),
                ["title"] = JsonValue.CreateStringValue(title),
                ["artist"] = JsonValue.CreateStringValue(
                    string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist),
                ["date"] = JsonValue.CreateStringValue(StringValue(release, "date")),
                ["country"] = JsonValue.CreateStringValue(StringValue(release, "country")),
                ["barcode"] = JsonValue.CreateStringValue(StringValue(release, "barcode")),
                ["front_cover"] = JsonValue.CreateBooleanValue(frontCover),
                ["tracks"] = tracks
            };
        }

        async Task EnsureArtworkAsync(
            StorageFolder folder,
            JsonObject metadata)
        {
            if (await HasCoverAsync(folder))
                return;

            var releaseId = StringValue(metadata, "release_id");
            var hasFront = BoolValue(metadata, "front_cover");

            if (hasFront &&
                !string.IsNullOrWhiteSpace(releaseId) &&
                await DownloadCoverArchiveAsync(folder, releaseId))
                return;

            await DownloadAppleArtworkAsync(
                folder,
                StringValue(metadata, "artist"),
                StringValue(metadata, "title"));
        }

        async Task<bool> DownloadCoverArchiveAsync(
            StorageFolder folder,
            string releaseId)
        {
            try
            {
                var response = await _http.GetAsync(
                    "https://coverartarchive.org/release/" +
                    Uri.EscapeDataString(releaseId) +
                    "/front-500");

                if (!response.IsSuccessStatusCode)
                    return false;

                var mediaType = response.Content.Headers.ContentType == null
                    ? ""
                    : response.Content.Headers.ContentType.MediaType ?? "";
                if (!mediaType.StartsWith("image/",
                    StringComparison.OrdinalIgnoreCase))
                    return false;

                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes == null || bytes.Length < 1024)
                    return false;

                await SaveCoverAsync(folder, bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        async Task<bool> DownloadAppleArtworkAsync(
            StorageFolder folder,
            string artist,
            string album)
        {
            if (string.IsNullOrWhiteSpace(album))
                return false;

            try
            {
                var term = string.Join(" ",
                    new[] { artist, album });
                var url =
                    "https://itunes.apple.com/search?entity=album&limit=12&term=" +
                    Uri.EscapeDataString(term);

                var json = await _http.GetStringAsync(url);
                JsonObject root;
                if (!JsonObject.TryParse(json, out root) ||
                    !root.ContainsKey("results") ||
                    root["results"].ValueType != JsonValueType.Array)
                    return false;

                JsonObject best = null;
                foreach (var value in root.GetNamedArray("results"))
                {
                    if (value.ValueType != JsonValueType.Object)
                        continue;

                    var row = value.GetObject();
                    var collection = StringValue(row, "collectionName");
                    var rowArtist = StringValue(row, "artistName");

                    if (string.Equals(
                            collection,
                            album,
                            StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(artist) ||
                         string.Equals(
                            rowArtist,
                            artist,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        best = row;
                        break;
                    }

                    if (best == null &&
                        collection.IndexOf(
                            album,
                            StringComparison.OrdinalIgnoreCase) >= 0)
                        best = row;
                }

                if (best == null)
                    return false;

                var artwork = StringValue(best, "artworkUrl100");
                if (string.IsNullOrWhiteSpace(artwork))
                    return false;

                artwork = artwork
                    .Replace("100x100bb", "600x600bb")
                    .Replace("100x100-75", "600x600-75");

                var response = await _http.GetAsync(artwork);
                if (!response.IsSuccessStatusCode)
                    return false;

                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes == null || bytes.Length < 1024)
                    return false;

                await SaveCoverAsync(folder, bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static async Task SaveCoverAsync(
            StorageFolder folder,
            byte[] bytes)
        {
            var file = await folder.CreateFileAsync(
                "cover.jpg",
                CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, bytes);
        }

        static async Task<bool> HasCoverAsync(StorageFolder folder)
        {
            try
            {
                var file = await folder.GetFileAsync("cover.jpg");
                return (await file.GetBasicPropertiesAsync()).Size > 1024;
            }
            catch
            {
                return false;
            }
        }

        static async Task<JsonObject> TryReadMetadataAsync(StorageFolder folder)
        {
            try
            {
                var file = await folder.GetFileAsync("metadata.json");
                JsonObject result;
                return JsonObject.TryParse(
                    await FileIO.ReadTextAsync(file),
                    out result)
                    ? result
                    : null;
            }
            catch
            {
                return null;
            }
        }

        static string BuildMusicBrainzDiscId(
            int firstTrack,
            int lastTrack,
            long leadoutLba,
            IDictionary<int, long> startLbas)
        {
            var canonical = new StringBuilder();
            canonical.Append(firstTrack.ToString("X2"));
            canonical.Append(lastTrack.ToString("X2"));
            canonical.Append((leadoutLba + 150L).ToString("X8"));

            for (var track = 1; track <= 99; track++)
            {
                long lba;
                var offset = startLbas.TryGetValue(track, out lba)
                    ? lba + 150L
                    : 0L;
                canonical.Append(offset.ToString("X8"));
            }

            var provider = HashAlgorithmProvider.OpenAlgorithm(
                HashAlgorithmNames.Sha1);
            var digest = provider.HashData(
                CryptographicBuffer.ConvertStringToBinary(
                    canonical.ToString(),
                    BinaryStringEncoding.Utf8));

            return CryptographicBuffer.EncodeToBase64String(digest)
                .Replace('+', '.')
                .Replace('/', '_')
                .Replace('=', '-');
        }

        static string ArtistCredit(JsonObject release)
        {
            if (release == null ||
                !release.ContainsKey("artist-credit") ||
                release["artist-credit"].ValueType != JsonValueType.Array)
                return "";

            var names = new List<string>();
            foreach (var value in release.GetNamedArray("artist-credit"))
            {
                if (value.ValueType != JsonValueType.Object)
                    continue;

                var row = value.GetObject();
                var name = StringValue(row, "name");
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }

            return string.Join("", names);
        }

        static string StringValue(
            JsonObject obj,
            string key,
            string fallback = "")
        {
            return obj != null &&
                   obj.ContainsKey(key) &&
                   obj[key].ValueType == JsonValueType.String
                ? obj[key].GetString()
                : fallback;
        }

        static double Number(
            JsonObject obj,
            string key,
            double fallback = 0)
        {
            return obj != null &&
                   obj.ContainsKey(key) &&
                   obj[key].ValueType == JsonValueType.Number
                ? obj[key].GetNumber()
                : fallback;
        }

        static bool BoolValue(JsonObject obj, string key)
        {
            return obj != null &&
                   obj.ContainsKey(key) &&
                   obj[key].ValueType == JsonValueType.Boolean &&
                   obj[key].GetBoolean();
        }
    }
}
