using BrimstoneXbox.Models;
using System;
using System.Collections.Generic;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace BrimstoneXbox.Services
{
    public sealed class PlaybackService
    {
        private static readonly Lazy<PlaybackService> _lazy =
            new Lazy<PlaybackService>(() => new PlaybackService());

        public static PlaybackService Instance => _lazy.Value;

        private readonly MediaPlayer _player;
        private MediaPlaybackList _playlist;
        private string _title = string.Empty;
        private string _artist = string.Empty;
        private string _album = string.Empty;

        public event EventHandler StateChanged;

        private PlaybackService()
        {
            _player = new MediaPlayer
            {
                AudioCategory = MediaPlayerAudioCategory.Media,
                AutoPlay = false,
                Volume = 0.70
            };

            _player.PlaybackSession.PlaybackStateChanged += (s, e) => RaiseStateChanged();
            _player.PlaybackSession.PositionChanged += (s, e) => RaiseStateChanged();
            _player.MediaEnded += (s, e) => RaiseStateChanged();
            _player.MediaFailed += (s, e) => RaiseStateChanged();
        }

        public MediaPlayer Player => _player;

        public void PlayUrl(string url, JsonObject source, double volume, double positionSeconds)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("Playback URL is required.", nameof(url));

            _playlist = null;

            var mediaSource = MediaSource.CreateFromUri(new Uri(url));
            var item = new MediaPlaybackItem(mediaSource);
            ApplyMetadata(item, source);

            _player.Source = item;
            _player.Volume = ClampVolume(volume);
            _player.IsMuted = false;

            if (positionSeconds > 0)
            {
                EventHandler<object> opened = null;
                opened = (sender, args) =>
                {
                    _player.MediaOpened -= opened;
                    try
                    {
                        _player.PlaybackSession.Position = TimeSpan.FromSeconds(positionSeconds);
                    }
                    catch
                    {
                    }
                };
                _player.MediaOpened += opened;
            }

            _player.Play();
            RaiseStateChanged();
        }

        public void PlayProgramme(IList<string> urls, JsonArray sources, double volume, double positionSeconds)
        {
            if (urls == null || urls.Count == 0)
                throw new ArgumentException("At least one playback URL is required.", nameof(urls));

            var list = new MediaPlaybackList
            {
                AutoRepeatEnabled = false,
                ShuffleEnabled = false
            };

            for (var i = 0; i < urls.Count; i++)
            {
                var item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(urls[i])));
                JsonObject source = null;

                if (sources != null && i < sources.Count && sources[i].ValueType == JsonValueType.Object)
                    source = sources[i].GetObject();

                ApplyMetadata(item, source);
                list.Items.Add(item);
            }

            list.CurrentItemChanged += OnCurrentItemChanged;
            _playlist = list;
            _player.Source = list;
            _player.Volume = ClampVolume(volume);
            _player.IsMuted = false;

            if (positionSeconds > 0)
            {
                EventHandler<object> opened = null;
                opened = (sender, args) =>
                {
                    _player.MediaOpened -= opened;
                    try
                    {
                        _player.PlaybackSession.Position = TimeSpan.FromSeconds(positionSeconds);
                    }
                    catch
                    {
                    }
                };
                _player.MediaOpened += opened;
            }

            _player.Play();
            UpdateCurrentMetadata();
            RaiseStateChanged();
        }

        public void Pause()
        {
            _player.Pause();
            RaiseStateChanged();
        }

        public void Resume()
        {
            _player.Play();
            RaiseStateChanged();
        }

        public void Stop()
        {
            _player.Pause();
            try
            {
                _player.PlaybackSession.Position = TimeSpan.Zero;
            }
            catch
            {
            }
            RaiseStateChanged();
        }

        public void Seek(double seconds)
        {
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
            RaiseStateChanged();
        }

        public void Next()
        {
            if (_playlist != null)
                _playlist.MoveNext();
        }

        public void Previous()
        {
            if (_playlist != null)
                _playlist.MovePrevious();
        }

        public PlaybackSnapshot Snapshot()
        {
            var session = _player.PlaybackSession;
            var state = session.PlaybackState;

            return new PlaybackSnapshot
            {
                State = StateName(state),
                Playing = state == MediaPlaybackState.Playing,
                Title = _title,
                Artist = _artist,
                Album = _album,
                PositionSeconds = session.Position.TotalSeconds,
                DurationSeconds = session.NaturalDuration.TotalSeconds,
                Volume = Math.Round(_player.Volume * 100.0, 1),
                Muted = _player.IsMuted
            };
        }

        public void SetVolume(double percent)
        {
            _player.Volume = ClampVolume(percent / 100.0);
            RaiseStateChanged();
        }

        public void SetMuted(bool muted)
        {
            _player.IsMuted = muted;
            RaiseStateChanged();
        }

        public void SaveState()
        {
        }

        private void ApplyMetadata(MediaPlaybackItem item, JsonObject source)
        {
            var title = JsonString(source, "title", JsonString(source, "track_title", "Brimstone"));
            var artist = JsonString(source, "artist", string.Empty);
            var album = JsonString(source, "album", JsonString(source, "album_title", string.Empty));

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

        private void OnCurrentItemChanged(MediaPlaybackList sender, CurrentMediaPlaybackItemChangedEventArgs args)
        {
            UpdateCurrentMetadata();
            RaiseStateChanged();
        }

        private void UpdateCurrentMetadata()
        {
            var item = _playlist?.CurrentItem;
            if (item == null)
                return;

            var props = item.GetDisplayProperties();
            _title = props.MusicProperties.Title ?? string.Empty;
            _artist = props.MusicProperties.Artist ?? string.Empty;
            _album = props.MusicProperties.AlbumTitle ?? string.Empty;
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private static double ClampVolume(double value)
        {
            if (double.IsNaN(value))
                return 0.70;
            return Math.Max(0, Math.Min(1, value));
        }

        private static string StateName(MediaPlaybackState state)
        {
            switch (state)
            {
                case MediaPlaybackState.Opening:
                    return "opening";
                case MediaPlaybackState.Buffering:
                    return "buffering";
                case MediaPlaybackState.Playing:
                    return "playing";
                case MediaPlaybackState.Paused:
                    return "paused";
                default:
                    return "idle";
            }
        }

        private static string JsonString(JsonObject obj, string key, string fallback)
        {
            if (obj == null || !obj.ContainsKey(key))
                return fallback;

            var value = obj[key];
            return value.ValueType == JsonValueType.String ? value.GetString() : fallback;
        }
    }
}
