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
        public List<CoreTrack> Tracks { get; } = new List<CoreTrack>();
        public string Meta => Tracks.Count > 0
            ? Tracks.Count + (Tracks.Count == 1 ? " track" : " tracks")
            : "Album";
    }

    public sealed class CoreTrack
    {
        public long Id { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public double DurationSeconds { get; set; }
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

    public sealed class IngestSummary
    {
        public bool AutoRip { get; set; }
        public int OpticalDrives { get; set; }
        public int ActiveJobs { get; set; }
        public string CurrentJob { get; set; }
    }

    public sealed class SooloosZone
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string State { get; set; }
        public double Volume { get; set; }
        public bool Muted { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
    }
}
