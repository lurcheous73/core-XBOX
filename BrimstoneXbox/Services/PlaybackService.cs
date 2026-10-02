using BrimstoneXbox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace BrimstoneXbox.Services
{
    public sealed class PlaybackService
    {
        static readonly Lazy<PlaybackService> Lazy =
            new Lazy<PlaybackService>(() => new PlaybackService());

        public static PlaybackService Instance => Lazy.Value;

        readonly MediaPlayer _player;
        MediaPlaybackList _playlist;
        string _title = "";
        string _artist = "";
        string _album = "";

        public event EventHandler StateChanged;

        PlaybackService()
        {
            _player = new MediaPlayer
            {
                AudioCategory = MediaPlayerAudioCategory.Media,
                AutoPlay = false,
                Volume = 0.70
            };

            _player.PlaybackSession.PlaybackStateChanged += (s,e) => Changed();
            _player.MediaEnded += (s,e) => Changed();
            _player.MediaFailed += (s,e) => Changed();
        }

        public void PlayUrl(string url, JsonObject source, double volume, double positionSeconds)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("Playback URL is required.");

            _playlist = null;
            var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(url)));
            ApplyMetadata(item, source);
            _player.Source = item;
            _player.Volume = Clamp(volume);
            _player.IsMuted = false;
            _player.Play();
            Changed();
        }

        public async Task PlayLocalFileAsync(
            string relativePath,
            JsonObject source,
            double volume,
            double positionSeconds)
        {
            var file = await ResolveLocalFileAsync(relativePath);
            _playlist = null;

            var item = new MediaPlaybackItem(MediaSource.CreateFromStorageFile(file));
            ApplyMetadata(item, source);
            _player.Source = item;
            _player.Volume = Clamp(volume);
            _player.IsMuted = false;
            _player.Play();

            if (positionSeconds > 0)
            {
                try
                {
                    _player.PlaybackSession.Position =
                        TimeSpan.FromSeconds(positionSeconds);
                }
                catch { }
            }

            Changed();
        }

        public async Task PlayLocalProgrammeAsync(
            IList<string> relativePaths,
            JsonArray sources,
            double volume,
            double positionSeconds)
        {
            if (relativePaths == null || relativePaths.Count == 0)
                throw new ArgumentException("At least one local track is required.");

            var list = new MediaPlaybackList();
            for (var i = 0; i < relativePaths.Count; i++)
            {
                var file = await ResolveLocalFileAsync(relativePaths[i]);
                var item = new MediaPlaybackItem(MediaSource.CreateFromStorageFile(file));

                JsonObject source = null;
                if (sources != null && i < sources.Count &&
                    sources[i].ValueType == JsonValueType.Object)
                    source = sources[i].GetObject();

                ApplyMetadata(item, source);
                list.Items.Add(item);
            }

            list.CurrentItemChanged += (s, e) =>
            {
                UpdateCurrentMetadata();
                Changed();
            };

            _playlist = list;
            _player.Source = list;
            _player.Volume = Clamp(volume);
            _player.IsMuted = false;
            _player.Play();
            UpdateCurrentMetadata();

            if (positionSeconds > 0)
            {
                try
                {
                    _player.PlaybackSession.Position =
                        TimeSpan.FromSeconds(positionSeconds);
                }
                catch { }
            }

            Changed();
        }

        public void PlayProgramme(IList<string> urls, JsonArray sources, double volume, double positionSeconds)
        {
            if (urls == null || urls.Count == 0)
                throw new ArgumentException("At least one playback URL is required.");

            var list = new MediaPlaybackList();
            for (var i = 0; i < urls.Count; i++)
            {
                var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(urls[i])));
                JsonObject source = null;
                if (sources != null && i < sources.Count &&
                    sources[i].ValueType == JsonValueType.Object)
                    source = sources[i].GetObject();

                ApplyMetadata(item, source);
                list.Items.Add(item);
            }

            list.CurrentItemChanged += (s,e) =>
            {
                UpdateCurrentMetadata();
                Changed();
            };

            _playlist = list;
            _player.Source = list;
            _player.Volume = Clamp(volume);
            _player.IsMuted = false;
            _player.Play();
            UpdateCurrentMetadata();
            Changed();
        }

        public void Pause() { _player.Pause(); Changed(); }
        public void Resume() { _player.Play(); Changed(); }

        public void Stop()
        {
            _player.Pause();
            try { _player.PlaybackSession.Position = TimeSpan.Zero; } catch { }
            Changed();
        }

        public void Seek(double seconds)
        {
            _player.PlaybackSession.Position =
                TimeSpan.FromSeconds(Math.Max(0, seconds));
            Changed();
        }

        public void Next() { if (_playlist != null) _playlist.MoveNext(); }
        public void Previous() { if (_playlist != null) _playlist.MovePrevious(); }

        public List<QueueItem> QueueSnapshot()
        {
            var result = new List<QueueItem>();

            if (_playlist != null)
            {
                var number = 1;
                foreach (var item in _playlist.Items)
                {
                    var props = item.GetDisplayProperties();
                    result.Add(new QueueItem
                    {
                        Number = number++.ToString(),
                        Title = props.MusicProperties.Title ?? "Track",
                        Artist = props.MusicProperties.Artist ?? "",
                        Album = props.MusicProperties.AlbumTitle ?? "",
                        DurationSeconds = 0
                    });
                }
                return result;
            }

            if (!string.IsNullOrWhiteSpace(_title))
            {
                result.Add(new QueueItem
                {
                    Number = "1",
                    Title = _title,
                    Artist = _artist,
                    Album = _album,
                    DurationSeconds = _player.PlaybackSession.NaturalDuration.TotalSeconds
                });
            }

            return result;
        }

        public PlaybackSnapshot Snapshot()
        {
            var session = _player.PlaybackSession;
            return new PlaybackSnapshot
            {
                State = StateName(session.PlaybackState),
                Playing = session.PlaybackState == MediaPlaybackState.Playing,
                Title = _title,
                Artist = _artist,
                Album = _album,
                PositionSeconds = session.Position.TotalSeconds,
                DurationSeconds = session.NaturalDuration.TotalSeconds,
                Volume = Math.Round(_player.Volume * 100, 1),
                Muted = _player.IsMuted
            };
        }

        public void SetVolume(double percent)
        {
            _player.Volume = Clamp(percent / 100.0);
            Changed();
        }

        public void SetMuted(bool muted)
        {
            _player.IsMuted = muted;
            Changed();
        }

        public void SaveState() { }

        static async Task<StorageFile> ResolveLocalFileAsync(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Local media path is required.");

            var absolute = Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            return await StorageFile.GetFileFromPathAsync(absolute);
        }

        void ApplyMetadata(MediaPlaybackItem item, JsonObject source)
        {
            var title = JsonString(source, "title",
                JsonString(source, "track_title", "Brimstone"));
            var artist = JsonString(source, "artist", "");
            var album = JsonString(source, "album",
                JsonString(source, "album_title", ""));

            var props = item.GetDisplayProperties();
            props.Type = Windows.Media.MediaPlaybackType.Music;
            props.MusicProperties.Title = title;
            props.MusicProperties.Artist = artist;
            props.MusicProperties.AlbumTitle = album;
            item.ApplyDisplayProperties(props);

            _title = title;
            _artist = artist;
            _album = album;
        }

        void UpdateCurrentMetadata()
        {
            var item = _playlist?.CurrentItem;
            if (item == null) return;
            var props = item.GetDisplayProperties();
            _title = props.MusicProperties.Title ?? "";
            _artist = props.MusicProperties.Artist ?? "";
            _album = props.MusicProperties.AlbumTitle ?? "";
        }

        void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);

        static double Clamp(double value) =>
            double.IsNaN(value) ? 0.70 : Math.Max(0, Math.Min(1, value));

        static string StateName(MediaPlaybackState state)
        {
            switch (state)
            {
                case MediaPlaybackState.Opening: return "opening";
                case MediaPlaybackState.Buffering: return "buffering";
                case MediaPlaybackState.Playing: return "playing";
                case MediaPlaybackState.Paused: return "paused";
                default: return "idle";
            }
        }

        static string JsonString(JsonObject obj, string key, string fallback)
        {
            if (obj == null || !obj.ContainsKey(key) ||
                obj[key].ValueType != JsonValueType.String)
                return fallback;
            return obj[key].GetString();
        }
    }
}
