using BrimstoneXbox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
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
        readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
        MediaPlaybackList _playlist;
        string _title = "";
        string _artist = "";
        string _album = "";
        string _audioOutputName = "Xbox system output";
        string _audioOutputId = "";
        bool _stopped;

        public event EventHandler StateChanged;

        public string AudioOutputName => _audioOutputName;
        public string AudioOutputId => _audioOutputId;

        PlaybackService()
        {
            _player = new MediaPlayer
            {
                AudioCategory = MediaPlayerAudioCategory.Media,
                AutoPlay = false,
                Volume = 0.70
            };

            _player.PlaybackSession.PlaybackStateChanged += (s,e) => Changed();
            _player.MediaEnded += (s,e) => { _stopped = true; Changed(); };
            _player.MediaFailed += (s,e) => Changed();
        }

        public async Task InitialisePreferredOutputAsync()
        {
            try
            {
                object savedValue;
                var savedId = _settings.Values.TryGetValue(
                    "xboxAudioOutputId", out savedValue)
                    ? savedValue as string ?? ""
                    : "";

                // AUTO deliberately leaves MediaPlayer on the Xbox system route.
                // That preserves the console's own HDMI + optical mirroring where
                // the hardware/console settings expose both outputs.
                if (string.IsNullOrWhiteSpace(savedId) ||
                    string.Equals(savedId, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _player.AudioDevice = null;
                    }
                    catch
                    {
                        // A fresh MediaPlayer is already on the system default route.
                    }

                    var devices = await DeviceInformation.FindAllAsync(
                        MediaDevice.GetAudioRenderSelector());

                    var hasHdmi = false;
                    var hasOptical = false;
                    foreach (var device in devices)
                    {
                        hasHdmi = hasHdmi || IsHdmi(device);
                        hasOptical = hasOptical || IsOptical(device);
                    }

                    _audioOutputId = "auto";
                    _audioOutputName = hasOptical
                        ? "HDMI + Optical / S/PDIF"
                        : hasHdmi
                            ? "HDMI"
                            : "Xbox system audio";

                    Changed();
                    return;
                }

                var outputs = await DeviceInformation.FindAllAsync(
                    MediaDevice.GetAudioRenderSelector());

                foreach (var device in outputs)
                {
                    if (!string.Equals(device.Id, savedId, StringComparison.Ordinal))
                        continue;

                    _player.AudioDevice = device;
                    _audioOutputId = device.Id ?? "";
                    _audioOutputName = FriendlyOutputName(device);
                    Changed();
                    return;
                }

                // A saved device disappeared. Fall back to AUTO instead of
                // silently routing to some unrelated endpoint.
                _settings.Values["xboxAudioOutputId"] = "auto";
                _audioOutputId = "auto";
                _audioOutputName = "Xbox system audio";
            }
            catch
            {
                _audioOutputId = "auto";
                _audioOutputName = "Xbox system audio";
            }

            Changed();
        }

        public async Task<List<JsonObject>> GetAudioOutputsAsync()
        {
            var result = new List<JsonObject>();
            var devices = await DeviceInformation.FindAllAsync(
                MediaDevice.GetAudioRenderSelector());

            foreach (var device in devices)
            {
                result.Add(new JsonObject
                {
                    ["id"] = JsonValue.CreateStringValue(device.Id ?? ""),
                    ["name"] = JsonValue.CreateStringValue(device.Name ?? ""),
                    ["preferred"] = JsonValue.CreateBooleanValue(
                        IsHdmi(device) || IsOptical(device)),
                    ["active"] = JsonValue.CreateBooleanValue(
                        string.Equals(device.Id, _audioOutputId, StringComparison.Ordinal))
                });
            }

            return result;
        }

        public async Task SetAudioOutputAsync(string deviceId)
        {
            _settings.Values["xboxAudioOutputId"] =
                string.IsNullOrWhiteSpace(deviceId) ? "auto" : deviceId;

            if (string.IsNullOrWhiteSpace(deviceId) ||
                string.Equals(deviceId, "auto", StringComparison.OrdinalIgnoreCase))
            {
                _settings.Values["xboxAudioOutputId"] = "auto";
                try { _player.AudioDevice = null; } catch { }
                await InitialisePreferredOutputAsync();
                return;
            }

            var devices = await DeviceInformation.FindAllAsync(
                MediaDevice.GetAudioRenderSelector());
            foreach (var device in devices)
            {
                if (!string.Equals(device.Id, deviceId, StringComparison.Ordinal))
                    continue;

                _player.AudioDevice = device;
                _audioOutputId = device.Id ?? "";
                _audioOutputName = FriendlyOutputName(device);
                Changed();
                return;
            }

            throw new InvalidOperationException("Requested Xbox audio output is not available.");
        }

        static bool IsHdmiOrOptical(DeviceInformation device) =>
            IsHdmi(device) || IsOptical(device);

        static bool IsHdmi(DeviceInformation device)
        {
            if (device == null) return false;
            var value = ((device.Name ?? "") + " " + (device.Id ?? "")).ToLowerInvariant();
            return value.Contains("hdmi");
        }

        static bool IsOptical(DeviceInformation device)
        {
            if (device == null) return false;
            var value = ((device.Name ?? "") + " " + (device.Id ?? "")).ToLowerInvariant();
            return value.Contains("optical") ||
                   value.Contains("spdif") ||
                   value.Contains("s/pdif") ||
                   value.Contains("toslink");
        }

        static string FriendlyOutputName(DeviceInformation device)
        {
            if (IsHdmi(device))
                return "HDMI";
            if (IsOptical(device))
                return "Optical / S/PDIF";
            return string.IsNullOrWhiteSpace(device?.Name)
                ? "Xbox system output"
                : device.Name;
        }

        public void PlayUrl(string url, JsonObject source, double volume, double positionSeconds)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("Playback URL is required.");

            _playlist = null;
            var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(url)));
            ApplyMetadata(item, source);
            _player.Source = item;
            _stopped = false;
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
            _stopped = false;
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

        public void Pause()
        {
            _stopped = false;
            _player.Pause();
            Changed();
        }

        public void Resume()
        {
            _stopped = false;
            _player.Play();
            Changed();
        }

        public void Stop()
        {
            _player.Pause();
            try { _player.PlaybackSession.Position = TimeSpan.Zero; } catch { }
            _stopped = true;
            Changed();
        }

        public void Seek(double seconds)
        {
            _player.PlaybackSession.Position =
                TimeSpan.FromSeconds(Math.Max(0, seconds));
            Changed();
        }

        public void Next()
        {
            _stopped = false;
            if (_playlist != null) _playlist.MoveNext();
            Changed();
        }

        public void Previous()
        {
            _stopped = false;
            if (_playlist != null) _playlist.MovePrevious();
            Changed();
        }

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
                State = _stopped ? "stopped" : StateName(session.PlaybackState),
                Playing = !_stopped && session.PlaybackState == MediaPlaybackState.Playing,
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
