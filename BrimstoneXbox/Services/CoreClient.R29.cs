using BrimstoneXbox.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage.Streams;

namespace BrimstoneXbox.Services
{
    public sealed partial class CoreClient
    {
        static JsonObject Object(JsonObject row, string key) =>
            row.ContainsKey(key) && row[key].ValueType == JsonValueType.Object ? row[key].GetObject() : new JsonObject();
        static JsonArray Array(JsonObject row, string key) =>
            row.ContainsKey(key) && row[key].ValueType == JsonValueType.Array ? row[key].GetArray() : null;
        static bool Bool(JsonObject row, string key, bool fallback = false) =>
            row.ContainsKey(key) && row[key].ValueType == JsonValueType.Boolean ? row[key].GetBoolean() : fallback;
        static IEnumerable<JsonObject> Rows(JsonArray rows) =>
            (rows ?? new JsonArray()).Where(v => v.ValueType == JsonValueType.Object).Select(v => v.GetObject());
        static string Text(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
        static string Part(string value) => Uri.EscapeDataString(value ?? "");

        public async Task<List<CoreAlbum>> PublishedMusicAsync()
        {
            var albums = new List<CoreAlbum>();
            string publication = null;
            var albumKeys = new HashSet<string>();
            foreach (var collection in new[] { "albums", "bootlegs", "mixtapes", "concerts", "radio_recordings" })
            {
                string cursor = null;
                var cursors = new HashSet<string>();
                do
                {
                    var root = await RequestAsync("/api/v1/library/collections/" + collection + "?limit=1000" +
                        (cursor == null ? "" : "&cursor=" + Part(cursor)));
                    var rows = Array(root, "albums");
                    if (rows == null || StringValue(root, "collection", collection) != collection)
                        throw new InvalidOperationException("Invalid Core music publication.");
                    var revision = StringValue(root, "revision") + ":" + StringValue(root, "projection_version");
                    if (publication != null && publication != revision)
                        throw new InvalidOperationException("Core's library changed. Refresh to load a complete publication.");
                    publication = revision;
                    foreach (var row in Rows(rows))
                    {
                        var album = new CoreAlbum {
                            Id = StringValue(row, "id"), Title = StringValue(row, "title", "Untitled"),
                            Artist = StringValue(row, "artist", "Unknown artist"), Collection = collection,
                            HasArtwork = Bool(row, "has_artwork") };
                        if (!albumKeys.Add(collection + ":" + album.Id))
                            throw new InvalidOperationException("Duplicate album in Core publication.");
                        foreach (var ed in Rows(Array(row, "editions")))
                        {
                            var edition = new CoreEdition { Title = Text(StringValue(ed, "title"), StringValue(ed, "source_format"), "Edition") };
                            foreach (var track in Rows(Array(ed, "tracks")))
                            {
                                var meta = Object(track, "metadata");
                                var item = Text(StringValue(track, "item_id"), StringValue(track, "id"));
                                if (string.IsNullOrWhiteSpace(item)) continue;
                                edition.Tracks.Add(new CoreTrack { ItemId = item, VariantId = StringValue(track, "variant_id"),
                                    Title = Text(StringValue(track, "title"), StringValue(meta, "title"), "Track"),
                                    Artist = Text(StringValue(track, "artist"), StringValue(meta, "artist"), album.Artist),
                                    Album = album.Title, DurationSeconds = NumberValue(track, "duration", 0) });
                            }
                            album.Editions.Add(edition);
                        }
                        if (album.Editions.Count > 0) album.Tracks.AddRange(album.Editions[0].Tracks);
                        albums.Add(album);
                    }
                    cursor = StringValue(root, "next_cursor");
                    if (cursor.Length == 0) cursor = null;
                    if (cursor != null && (!cursors.Add(cursor) || cursors.Count > 100))
                        throw new InvalidOperationException("Invalid Core catalogue pagination.");
                } while (cursor != null);
            }
            return albums;
        }

        public async Task<List<MediaCard>> RadioAsync()
        {
            foreach (var path in new[] { "/api/v1/me/radio/favourites", "/api/v1/me/radio/recent", "/api/v1/radio/popular" })
            {
                var root = await RequestAsync(path);
                var rows = Array(root, "stations") ?? Array(root, "items") ?? Array(root, "favourites") ?? Array(root, "recent");
                if (rows == null || root.ContainsKey("error") || !Bool(root, "ok", true))
                    throw new InvalidOperationException("Invalid Core Radio catalogue.");
                var cards = new List<MediaCard>();
                var seen = new HashSet<string>();
                foreach (var wrapper in Rows(rows))
                {
                    var row = wrapper.ContainsKey("station") ? Object(wrapper, "station") : wrapper;
                    var url = Text(StringValue(row, "url_resolved"), StringValue(row, "url"));
                    Uri uri;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                        (uri.Scheme != "http" && uri.Scheme != "https") || !seen.Add(url)) continue;
                    cards.Add(new MediaCard { Kind = "radio", Id = Text(StringValue(row, "id"), StringValue(row, "stationuuid"), url),
                        Title = StringValue(row, "name", "Radio"), StreamUrl = url,
                        Artist = StringValue(row, "country"), Meta = string.Join(" · ", new[] {
                            StringValue(row, "language"), StringValue(row, "codec").ToUpperInvariant(),
                            StringValue(row, "bitrate") }.Where(v => v.Length > 0)) });
                }
                if (cards.Count > 0) { RadioSource = path.Contains("favourites") ? "Favourites" : path.Contains("recent") ? "Recent" : "Popular"; return cards; }
            }
            RadioSource = "Popular";
            return new List<MediaCard>();
        }

        public string RadioSource { get; private set; } = "";

        public async Task PlayRadioAsync(MediaCard station, string endpoint)
        {
            var registry = await RequestAsync("/api/v1/streaming/radio", "POST", new JsonObject {
                ["name"] = JsonValue.CreateStringValue(station.Title), ["url"] = JsonValue.CreateStringValue(station.StreamUrl),
                ["country"] = JsonValue.CreateStringValue(station.Artist ?? "") });
            var id = StringValue(Object(registry, "station"), "id");
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Core did not register this station.");
            await RequestAsync("/api/v1/streaming/play", "POST", new JsonObject {
                ["provider"] = JsonValue.CreateStringValue("internet_radio"), ["item_id"] = JsonValue.CreateStringValue(id),
                ["endpoint_id"] = JsonValue.CreateStringValue(endpoint), ["device"] = JsonValue.CreateStringValue("default") });
        }

        public async Task<List<MediaCard>> VideosAsync()
        {
            var root = await RequestAsync("/api/v1/concert-videos");
            var rows = Array(root, "items");
            if (rows == null) throw new InvalidOperationException("Invalid Core video catalogue.");
            var result = new List<MediaCard>();
            foreach (var row in Rows(rows))
            {
                var id = StringValue(row, "id");
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^(?:variant_)?[a-f0-9]{32}$")) continue;
                var video = Object(row, "video");
                var albumId = StringValue(row, "artwork_album_id");
                var sha = StringValue(Object(row, "artwork"), "sha256");
                var card = new MediaCard { Id = id, Kind = "video", Title = StringValue(row, "title", "Video"),
                    Artist = StringValue(row, "artist", "Unknown artist"), VariantId = StringValue(row, "variant_id", id),
                    Available = Bool(row, "available", true), Approved = StringValue(row, "approval_state", "approved") == "approved",
                    Meta = string.Join(" · ", new[] { StringValue(row, "album"), StringValue(row, "show"),
                        StringValue(row, "performance_date"), StringValue(row, "venue"), StringValue(row, "format").ToUpperInvariant(),
                        NumberValue(video, "height", 0) > 0 ? StringValue(video, "height") + "p" : "",
                        StringValue(video, "codec").ToUpperInvariant() }.Where(v => v.Length > 0).Distinct()) };
                if (System.Text.RegularExpressions.Regex.IsMatch(albumId, "^[A-Za-z0-9._-]{1,200}$") &&
                    System.Text.RegularExpressions.Regex.IsMatch(sha, "^[a-f0-9]{64}$"))
                    card.ArtworkPath = "/api/v1/library/catalogue/albums/" + Part(albumId) + "/artwork-cache/" + sha;
                result.Add(card);
            }
            return result.OrderByDescending(c => (c.Available ? 2 : 0) + (c.Approved ? 1 : 0))
                .GroupBy(c => c.VariantId).Select(g => g.First()).OrderBy(c => c.Artist).ThenBy(c => c.Title).ToList();
        }

        public async Task<string> VideoStreamAsync(MediaCard video, bool compatible)
        {
            var ticket = await RequestAsync("/api/v1/concert-videos/" + Part(video.Id) +
                "/stream-ticket" + (compatible ? "?delivery=browser" : ""));
            var path = StringValue(ticket, "path");
            Uri route;
            if (StringValue(ticket, "id") != video.Id || !Uri.TryCreate(path, UriKind.Relative, out route) ||
                !path.StartsWith("/api/v1/concert-videos/" + video.Id + "/stream?token=", StringComparison.Ordinal) ||
                path.Contains("#") || path.Contains("&") || path.Length > 8400 || path.EndsWith("token=") ||
                path.Contains(UserToken))
                throw new InvalidOperationException("Core returned an invalid video ticket.");
            var target = new Uri(new Uri(BaseUrl), path);
            return target.AbsoluteUri;
        }

        public async Task PlayTracksAsync(string endpoint, IList<CoreTrack> tracks, bool append)
        {
            if (tracks.Count == 0) throw new InvalidOperationException("This edition has no playable tracks.");
            var items = new JsonArray();
            var ids = new JsonArray();
            var variants = new JsonObject();
            var outputs = await RequestAsync("/api/v1/endpoints");
            var native = Rows(Array(outputs, "endpoints")).Any(e => StringValue(e, "id") == endpoint &&
                StringValue(Object(e, "capabilities"), "control_api") == "core-sooloos-v1");
            foreach (var track in tracks)
            {
                if (string.IsNullOrWhiteSpace(track.ItemId)) throw new InvalidOperationException("Missing catalogue track identity.");
                var item = new JsonObject { ["item_id"] = JsonValue.CreateStringValue(track.ItemId) };
                if (!native && !string.IsNullOrWhiteSpace(track.VariantId)) {
                    item["variant_id"] = JsonValue.CreateStringValue(track.VariantId);
                    variants[track.ItemId] = JsonValue.CreateStringValue(track.VariantId);
                }
                items.Add(item); ids.Add(JsonValue.CreateStringValue(track.ItemId));
            }
            var payload = append
                ? new JsonObject { ["item_ids"] = ids, ["variant_ids"] = variants }
                : new JsonObject { ["items"] = items, ["device"] = JsonValue.CreateStringValue("default"),
                    ["position_seconds"] = JsonValue.CreateNumberValue(0) };
            var reply = await RequestAsync("/api/v1/endpoints/" + Part(endpoint) + "/catalogue/queue/" + (append ? "append" : "play"), "POST", payload);
            if (native && (!Bool(reply, "ok") || !Bool(Object(reply, "result"), "confirmed")))
                throw new InvalidOperationException("Sooloos has not confirmed the queue.");
        }

        public Task<JsonObject> ClearQueueAsync(string endpoint) =>
            RequestAsync("/api/v1/sessions/" + Part(endpoint) + "/catalogue/queue", "DELETE");

        public async Task<List<QueueItem>> QueueAsync(string endpoint)
        {
            var root = await RequestAsync("/api/v1/sessions/" + Part(endpoint) + "/catalogue/queue");
            var rows = Array(root, "queue") ?? Array(root, "items") ?? Array(Object(root, "result"), "queue");
            return Rows(rows).Select((r, i) => new QueueItem { Number = (i + 1).ToString(),
                Title = StringValue(r, "title", "Track"), Artist = StringValue(r, "artist"), Album = StringValue(r, "album") }).ToList();
        }

        public Task<JsonObject> ControlAsync(string endpoint, string action) =>
            RequestAsync("/api/v1/playback/" + Part(endpoint) + "/control/" + Part(action), "POST");

        public async Task<List<MediaCard>> SpokenAsync(bool books)
        {
            var root = await RequestAsync(books ? "/api/v1/library/audiobooks?limit=5000" : "/api/v1/me/podcasts");
            return Rows(Array(root, books ? "items" : "subscriptions")).Select(row => new MediaCard {
                Kind = books ? "book" : "podcast", Id = Text(StringValue(row, "item_id"), StringValue(row, "id")),
                Title = Text(StringValue(row, "title"), StringValue(row, "name"), StringValue(Object(row, "metadata"), "book_title"), "Spoken word"),
                Artist = Text(StringValue(row, "artist"), StringValue(Object(row, "metadata"), "author")),
                Meta = books ? "Audiobook" : "Podcast" }).ToList();
        }

        public async Task<List<MediaCard>> EpisodesAsync(string subscription)
        {
            var root = await RequestAsync("/api/v1/me/podcasts/" + Part(subscription));
            return Rows(Array(root, "episodes")).Select(row => new MediaCard { Kind = "episode",
                Id = StringValue(row, "id"), VariantId = subscription, Title = StringValue(row, "title", "Episode"),
                Meta = Text(StringValue(row, "published_at"), StringValue(row, "published")) }).ToList();
        }

        public Task<JsonObject> PlayEpisodeAsync(MediaCard episode, string endpoint) =>
            RequestAsync("/api/v1/me/podcasts/" + Part(episode.VariantId) + "/episodes/" + Part(episode.Id) + "/play", "POST",
                new JsonObject { ["endpoint_id"] = JsonValue.CreateStringValue(endpoint) });

        public async Task LoadArtworkAsync(MediaCard card)
        {
            if (string.IsNullOrWhiteSpace(card.ArtworkPath)) return;
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUrl + card.ArtworkPath)))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", UserToken);
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 5242880) return;
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = new InMemoryRandomAccessStream())
                    {
                        var buffer = new byte[8192];
                        var total = 0;
                        using (var writer = new DataWriter(output.GetOutputStreamAt(0)))
                        {
                            int count;
                            while ((count = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                total += count;
                                if (total > 5242880) return;
                                writer.WriteBytes(count == buffer.Length ? buffer : buffer.Take(count).ToArray());
                            }
                            await writer.StoreAsync();
                            await card.Artwork.SetSourceAsync(output);
                        }
                    }
                }
            }
        }
    }
}
