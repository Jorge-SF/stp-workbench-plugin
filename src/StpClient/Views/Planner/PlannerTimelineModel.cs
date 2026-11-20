// Adapter from the planner view model (StpPlanner.PlannerSession) to the timeline control's model
// (StpTimeline.ITimelineModel): group headers and activity rows in display order, one block per visible day, and the
// precedence links between shown rows. Read-only for now: no drag or editing goes back to the session.
//
// No Workbench types and no Resources: src/StpClient.Tests compiles this file directly (no WPF window needed).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using Maptek.Vulcan.Stp.Planner;
using Maptek.Vulcan.Stp.Timeline.Model;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>
    /// <see cref="ITimelineModel"/> over a <see cref="PlannerSession"/>. Rebuilt (and <see cref="TimelineModel.Changed"/>
    /// raised) whenever the session's rows change; dispose it to stop listening.
    /// </summary>
    public sealed class PlannerTimelineModel : TimelineModel, IDisposable
    {
        /// <summary>Colour of group headers that are not activity types (level, resource, none).</summary>
        public const string NeutralGroupColor = "#66727F";

        /// <summary>Prefix of group header keys, so they never collide with activity ids.</summary>
        public const string GroupKeyPrefix = "group:";

        private readonly PlannerSession session;

        public PlannerTimelineModel(PlannerSession session)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            Rebuild();
            session.PropertyChanged += OnSessionChanged;
        }

        /// <summary>Number of rebuilds so far (tests).</summary>
        public int BuildCount { get; private set; }

        public void Dispose() => session.PropertyChanged -= OnSessionChanged;

        /// <summary>Re-reads the session and raises <see cref="TimelineModel.Changed"/>.</summary>
        public void Rebuild()
        {
            Fill(this, session);
            BuildCount++;
            NotifyChanged();
        }

        /// <summary>Fills <paramref name="model"/> from the session's current view (pure; no events).</summary>
        public static void Fill(TimelineModel model, PlannerSession session)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (session == null) throw new ArgumentNullException(nameof(session));

            model.StartDate = ParseDay(session.RangeStart) ?? session.Snapshot.ExportedAt.Date;
            model.DayCount = Math.Max(session.Days.Count, 1);
            model.CompletedThrough = session.Snapshot.ExportedAt;

            model.Rows.Clear();
            bool grouped = session.Grouping != PlannerGrouping.None;
            foreach (var group in session.Groups)
            {
                if (grouped)
                {
                    string color = session.Grouping == PlannerGrouping.Type ? session.TypeColor(group.Key) : NeutralGroupColor;
                    string title = string.IsNullOrEmpty(group.Label) ? "—" : group.Label;
                    model.Rows.Add(TimelineRow.Group(GroupKeyPrefix + group.Key, title, ToColor(color), group.Count));
                }

                foreach (var row in group.Rows) model.Rows.Add(ToRow(row, session.TypeColor(row.Type), session.ActualProgressBoxes));
            }

            model.Links.Clear();
            var shown = new HashSet<string>(model.Rows.Where(r => r.Kind == TimelineRowKind.Activity).Select(r => r.Key), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in session.Snapshot.Precedences)
            {
                if (!shown.Contains(link.PredecessorId) || !shown.Contains(link.SuccessorId)) continue;
                if (link.PredecessorId == link.SuccessorId) continue;
                if (seen.Add(link.PredecessorId + "\u0001" + link.SuccessorId)) model.Links.Add(new TimelineLink(link.PredecessorId, link.SuccessorId));
            }
        }

        /// <summary>One activity row: one block per day, labelled with the day's quantity.</summary>
        public static TimelineRow ToRow(PlannerRow row, string color, IReadOnlyDictionary<string, IReadOnlyList<ActualProgressBox>> progress = null)
        {
            var result = new TimelineRow
            {
                Kind = TimelineRowKind.Activity,
                Key = row.ActivityId,
                Id = row.ActivityId,
                Name = row.Name,
                Resource = row.Resource,
                Plan = row.PlanText,
                Color = ToColor(color),
                IsLocked = row.IsLocked,
                Move = row.Status == SegmentStatus.Direct ? TimelineMoveKind.Direct
                    : row.Status == SegmentStatus.Cascade ? TimelineMoveKind.Cascade
                    : TimelineMoveKind.None,
            };
            foreach (var day in (row.Segments ?? Array.Empty<DaySegment>()).GroupBy(s => s.DayIndex).OrderBy(g => g.Key))
            {
                var segments = day.ToList();
                bool completed = segments.All(s => s.Status == SegmentStatus.Completed);
                var amounts = segments.Where(s => s.Amount.HasValue).ToList();
                double? amount = amounts.Count == 0 ? (double?)null : amounts.Sum(s => s.Amount.Value);
                string label = amount.HasValue ? PlannerProjection.CompactQuantity(amount.Value) : null;
                // Tag: the day's first pending segment (fragment id, logical unit) that PlannerEditRouter edits.
                var tag = segments.FirstOrDefault(s => s.Status != SegmentStatus.Completed) ?? segments[0];
                // "Exentar Límite" is per day (logical unit): green when every pending segment of the day is exempt.
                bool exempt = !completed && segments.Where(s => s.Status != SegmentStatus.Completed).All(s => s.IsCapacityExempt);
                result.Blocks.Add(new TimelineBlock(day.Key, label, completed, tag, BlockDetail(tag, PlannerProjection.QuantityText(amount, row.Unit)), exempt));
            }

            // "Avance real" boxes (only while the mode is on; the session leaves the map empty otherwise).
            if (progress != null && row.ActivityId != null && progress.TryGetValue(row.ActivityId, out var boxes))
            {
                foreach (var box in boxes) result.ProgressBoxes.Add(new TimelineProgressBox(box.DayIndex, box.Value, box.Text, box));
            }

            return result;
        }

        /// <summary>Block tooltip detail: quantity, equipment (marked when assigned locally) and the capacity exemption.</summary>
        public static string BlockDetail(DaySegment segment, string quantityText)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(quantityText)) parts.Add(quantityText);
            if (!string.IsNullOrEmpty(segment?.Resource)) parts.Add(segment.HasResourceOverride ? segment.Resource + " (asignado)" : segment.Resource);
            if (segment != null && segment.IsCapacityExempt) parts.Add("exento del límite");
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }

        /// <summary><c>#RRGGBB</c> to a colour; anything else gives the palette fallback.</summary>
        public static Color ToColor(string hex)
        {
            if (TryParse(hex, out var color)) return color;
            TryParse(TypePalette.Fallback, out color);
            return color;
        }

        private static bool TryParse(string hex, out Color color)
        {
            color = Colors.Gray;
            if (hex == null || hex.Length != 7 || hex[0] != '#') return false;
            if (!int.TryParse(hex.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return false;
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }

        private static DateTime? ParseDay(string day) =>
            DateTime.TryParseExact(day ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : (DateTime?)null;

        private void OnSessionChanged(object sender, PropertyChangedEventArgs e)
        {
            // PlannerSession raises every view property after each change; Rows is raised exactly once per change.
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(PlannerSession.Rows)) Rebuild();
        }
    }
}
