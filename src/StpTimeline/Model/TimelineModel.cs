using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Maptek.Vulcan.Stp.Timeline.Model
{
    /// <summary>Simple mutable implementation of <see cref="ITimelineModel"/> (preview, tests, or as a base).</summary>
    public class TimelineModel : ITimelineModel
    {
        public DateTime StartDate { get; set; } = DateTime.Today;
        public int DayCount { get; set; } = 14;
        public DateTime? CompletedThrough { get; set; }
        public List<TimelineRow> Rows { get; } = new List<TimelineRow>();
        public List<TimelineLink> Links { get; } = new List<TimelineLink>();

        IReadOnlyList<ITimelineRow> ITimelineModel.Rows => Rows;
        IReadOnlyList<ITimelineLink> ITimelineModel.Links => Links;

        public event EventHandler Changed;

        /// <summary>Tell the control to re-read the model.</summary>
        public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    public class TimelineRow : ITimelineRow, ITimelineProgressRow
    {
        public TimelineRowKind Kind { get; set; }
        public string Key { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Resource { get; set; }
        public string Plan { get; set; }
        public Color Color { get; set; } = Colors.SteelBlue;
        public bool IsLocked { get; set; }
        public TimelineMoveKind Move { get; set; }
        public int GroupCount { get; set; }
        public List<TimelineBlock> Blocks { get; } = new List<TimelineBlock>();

        /// <summary>Gets the "Avance real" value boxes, sorted by day.</summary>
        public List<TimelineProgressBox> ProgressBoxes { get; } = new List<TimelineProgressBox>();

        IReadOnlyList<TimelineProgressBox> ITimelineProgressRow.ProgressBoxes => ProgressBoxes;

        IReadOnlyList<TimelineBlock> ITimelineRow.Blocks => Blocks;

        public static TimelineRow Group(string key, string title, Color color, int count) =>
            new TimelineRow { Kind = TimelineRowKind.Group, Key = key, Name = title, Color = color, GroupCount = count };
    }

    public class TimelineLink : ITimelineLink
    {
        public TimelineLink(string fromKey, string toKey)
        {
            FromKey = fromKey;
            ToKey = toKey;
        }

        public string FromKey { get; }
        public string ToKey { get; }
    }
}
