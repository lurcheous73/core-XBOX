using System.Collections.Generic;

namespace BrimstoneXbox.Models
{
    public sealed class CoreAlbum
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public List<CoreTrack> Tracks { get; } = new List<CoreTrack>();
        public string Meta => Tracks.Count + (Tracks.Count == 1 ? " track" : " tracks");
    }

    public sealed class CoreTrack
    {
        public long Id { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public double DurationSeconds { get; set; }

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
}
