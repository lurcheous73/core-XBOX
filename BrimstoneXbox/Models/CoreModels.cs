using Windows.UI.Xaml.Media.Imaging;
using System.Collections.Generic;

namespace BrimstoneXbox.Models
{
    public sealed class CoreAlbum
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public BitmapImage Artwork { get; set; }
        public System.DateTimeOffset AddedAt { get; set; }
        public bool IsFavourite { get; set; }
        public string SourceKind { get; set; }
        public string Collection { get; set; } = "albums";
        public bool HasArtwork { get; set; }
        public List<CoreEdition> Editions { get; } = new List<CoreEdition>();
        public List<CoreTrack> Tracks { get; } = new List<CoreTrack>();
        public string Meta => Tracks.Count > 0
            ? Tracks.Count + (Tracks.Count == 1 ? " track" : " tracks")
            : "Album";
    }

    public sealed class CoreTrack
    {
        public long Id { get; set; }
        public string ItemId { get; set; }
        public string VariantId { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public double DurationSeconds { get; set; }
        public double StartSeconds { get; set; }
        public double DurationLimitSeconds { get; set; }
        public string LocalPath { get; set; }

        public string DurationText
        {
            get
            {
                var seconds = (int)System.Math.Max(0, DurationSeconds);
                return (seconds / 60).ToString() + ":" + (seconds % 60).ToString("00");
            }
        }
    }

    public sealed class PlaybackSnapshot
    {
        public string State { get; set; }
        public bool Playing { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public double PositionSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public double Volume { get; set; }
        public bool Muted { get; set; }
    }

    public sealed class QueueItem
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public double DurationSeconds { get; set; }
        public string Number { get; set; }

        public string Meta
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Artist)) parts.Add(Artist);
                if (!string.IsNullOrWhiteSpace(Album)) parts.Add(Album);
                return string.Join(" · ", parts);
            }
        }

        public string DurationText
        {
            get
            {
                var seconds = (int)System.Math.Max(0, DurationSeconds);
                return seconds > 0
                    ? (seconds / 60).ToString() + ":" + (seconds % 60).ToString("00")
                    : "";
            }
        }
    }

    public sealed class CoreEdition
    {
        public string Title { get; set; }
        public List<CoreTrack> Tracks { get; } = new List<CoreTrack>();
    }

    public sealed class MediaCard
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Meta { get; set; }
        public BitmapImage Artwork { get; } = new BitmapImage { DecodePixelWidth = 768 };
        public string ArtworkPath { get; set; }
        public CoreAlbum Album { get; set; }
        public string StreamUrl { get; set; }
        public string VariantId { get; set; }
        public bool Available { get; set; } = true;
        public bool Approved { get; set; } = true;
    }

}
