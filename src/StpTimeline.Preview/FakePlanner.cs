using System;
using System.Collections.Generic;
using System.Linq;
using Maptek.Vulcan.Stp.Timeline.Layout;
using Maptek.Vulcan.Stp.Timeline.Model;

namespace Maptek.Vulcan.Stp.Timeline.Preview
{
    /// <summary>
    /// Stand-in for PlannerSession + engine so the editing interactions can be tried without Vulcan. It applies the
    /// timeline's requests to the <see cref="TimelineModel"/> with a few of the engine's rules, simplified:
    /// moved work must stay in the horizon, must not land on or before completed work or the activity's earlier
    /// days, and must not start before its predecessors finish (incoming precedence). Successors are pushed by a
    /// cascade. Undo / redo keep snapshots. It is NOT the real scheduling logic.
    /// </summary>
    internal sealed class FakePlanner
    {
        private readonly TimelineModel _model;
        private readonly Dictionary<string, TimelineRow> _byKey;
        private readonly Dictionary<string, List<string>> _successors = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, List<string>> _predecessors = new Dictionary<string, List<string>>();
        private readonly Stack<Snapshot> _undo = new Stack<Snapshot>(), _redo = new Stack<Snapshot>();
        private readonly HashSet<string> _exempt = new HashSet<string>(StringComparer.Ordinal);

        public FakePlanner(TimelineModel model)
        {
            _model = model;
            _byKey = model.Rows.Where(r => r.Kind == TimelineRowKind.Activity).ToDictionary(r => r.Key, StringComparer.Ordinal);
            foreach (var link in model.Links)
            {
                Add(_successors, link.FromKey, link.ToKey);
                Add(_predecessors, link.ToKey, link.FromKey);
            }
        }

        private static void Add(Dictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
            list.Add(value);
        }

        /// <summary>Outcome of the last request, for the status bar.</summary>
        public string LastOutcome { get; private set; } = "—";

        /// <summary>Handle any of the timeline's request events.</summary>
        public void Handle(TimelineEditEventArgs e)
        {
            switch (e.Action)
            {
                case TimelineEditAction.Move: Run(e, () => Move(e)); break;
                case TimelineEditAction.DeferToNextPeriod: Run(e, () => Defer(e)); break;
                case TimelineEditAction.SplitAndStagger: Run(e, () => Stagger(e)); break;
                case TimelineEditAction.AssignResource: Run(e, () => Assign(e)); break;
                case TimelineEditAction.ToggleCapacityExemption: Run(e, () => ToggleExemption(e)); break;
                case TimelineEditAction.Undo: UndoRedo(e, _undo, _redo, "Deshacer"); break;
                case TimelineEditAction.Redo: UndoRedo(e, _redo, _undo, "Rehacer"); break;
            }
        }

        /// <summary>Show the capacity exemption entry, labelled by the activity's state.</summary>
        public void OnMenuOpening(TimelineMenuEventArgs e)
        {
            var item = e.Find(TimelineEditAction.ToggleCapacityExemption);
            item.IsVisible = true;
            item.Header = _exempt.Contains(e.Row.Key) ? "Quitar exención" : "Exentar Límite";
        }

        /// <summary>Apply an edit atomically: snapshot, try, and roll back + reject when a rule fails.</summary>
        private void Run(TimelineEditEventArgs e, Func<string> edit)
        {
            var before = Snapshot.Take(_model);
            string message;
            try
            {
                message = edit();
            }
            catch (Rejected r)
            {
                before.Restore(_model);
                e.Reject(r.Message);
                LastOutcome = "rechazado: " + r.Message;
                return;
            }
            _undo.Push(before);
            _redo.Clear();
            LastOutcome = message;
            _model.NotifyChanged();
        }

        private void UndoRedo(TimelineEditEventArgs e, Stack<Snapshot> from, Stack<Snapshot> to, string name)
        {
            if (from.Count == 0) { e.Reject(name + ": no hay cambios."); LastOutcome = name + ": no hay cambios"; return; }
            to.Push(Snapshot.Take(_model));
            from.Pop().Restore(_model);
            LastOutcome = name;
            _model.NotifyChanged();
        }

        private sealed class Rejected : Exception
        {
            public Rejected(string message) : base(message) { }
        }

        private string Move(TimelineEditEventArgs e)
        {
            var row = _byKey[e.ActivityKey];
            int from = e.Scope == TimelineMoveScope.Tail ? e.BlockIndex : FirstPending(row);
            Shift(row, from, e.DayDelta, direct: true);
            int pushed = Cascade(row.Key);
            string what = e.Scope == TimelineMoveScope.Tail ? $"{row.Id} desde {e.Date:dd/MM}" : $"{row.Id} completa";
            return $"movida {what} {TimelineEditMath.DeltaText(e.DayDelta)}" + (pushed > 0 ? $" · {pushed} en cascada" : string.Empty);
        }

        private string Defer(TimelineEditEventArgs e)
        {
            var row = _byKey[e.ActivityKey];
            int delta = _model.DayCount - row.Blocks[e.BlockIndex].Day;
            Shift(row, e.BlockIndex, delta, direct: true, allowOutside: true);
            Cascade(row.Key, allowOutside: true);
            return $"{row.Id}: desde {e.Date:dd/MM} enviada al siguiente periodo";
        }

        /// <summary>staggerDailyTail: for k = 1..divisions the tail from day (selected + k) moves gapDays more.</summary>
        private string Stagger(TimelineEditEventArgs e)
        {
            var row = _byKey[e.ActivityKey];
            int count = 0;
            for (int k = 1; k <= e.Divisions; k++)
            {
                int index = e.BlockIndex + k;
                if (index >= row.Blocks.Count) break;
                Shift(row, index, e.GapDays, direct: true);
                count++;
            }
            if (count == 0) throw new Rejected("No hay días posteriores para dividir.");
            Cascade(row.Key);
            return $"{row.Id}: dividida en {count + 1} partes con {e.GapDays} día(s) de desfase";
        }

        private string Assign(TimelineEditEventArgs e)
        {
            var row = _byKey[e.ActivityKey];
            row.Resource = row.Resource.EndsWith(" (alterno)", StringComparison.Ordinal) ? row.Resource.Substring(0, row.Resource.Length - 10) : row.Resource + " (alterno)";
            return $"{row.Id}: equipo {row.Resource}";
        }

        private string ToggleExemption(TimelineEditEventArgs e)
        {
            bool exempt = !_exempt.Remove(e.ActivityKey);
            if (exempt) _exempt.Add(e.ActivityKey);
            return $"{e.ActivityKey}: " + (exempt ? "exenta del límite" : "sin exención");
        }

        /// <summary>Shift the pending blocks from <paramref name="fromIndex"/> on, enforcing the simplified rules.</summary>
        private void Shift(TimelineRow row, int fromIndex, int delta, bool direct, bool allowOutside = false)
        {
            var blocks = row.Blocks;
            for (int k = fromIndex; k < blocks.Count; k++)
            {
                if (blocks[k].IsCompleted) throw new Rejected("El avance completado está protegido y no se puede mover.");
            }
            if (fromIndex >= blocks.Count) return;
            int newFirst = blocks[fromIndex].Day + delta;
            int newLast = blocks[blocks.Count - 1].Day + delta;
            if (!allowOutside && (newFirst < 0 || newLast > _model.DayCount - 1))
                throw new Rejected("El movimiento saldría del horizonte visible. Extienda el rango o reduzca el desplazamiento.");
            if (fromIndex > 0 && newFirst <= blocks[fromIndex - 1].Day)
                throw new Rejected(blocks[fromIndex - 1].IsCompleted
                    ? "El avance completado está protegido y no se puede mover."
                    : "El movimiento no es válido: los días no pueden adelantarse a los días anteriores de la actividad.");
            if (direct && delta < 0 && fromIndex == FirstPending(row))
            {
                foreach (var pred in Predecessors(row.Key))
                    if (newFirst <= LastDay(pred))
                        throw new Rejected($"El movimiento viola una precedencia entrante ({pred.Id}). Revise las actividades predecesoras.");
            }
            for (int k = fromIndex; k < blocks.Count; k++)
            {
                var b = blocks[k];
                blocks[k] = new TimelineBlock(b.Day + delta, b.Label, b.IsCompleted, b.Tag, b.ToolTip);
            }
            if (direct) row.Move = TimelineMoveKind.Direct;
        }

        private static int FirstPending(TimelineRow row)
        {
            for (int k = 0; k < row.Blocks.Count; k++) if (!row.Blocks[k].IsCompleted) return k;
            return row.Blocks.Count;
        }

        private IEnumerable<TimelineRow> Predecessors(string key) =>
            _predecessors.TryGetValue(key, out var list) ? list.Where(_byKey.ContainsKey).Select(k => _byKey[k]) : Enumerable.Empty<TimelineRow>();

        private static int LastDay(TimelineRow row) => row.Blocks.Count == 0 ? int.MinValue : row.Blocks[row.Blocks.Count - 1].Day;

        /// <summary>Push successors that would now start on or before a predecessor's last day (finish-start, no lag).</summary>
        private int Cascade(string key, bool allowOutside = false)
        {
            var queue = new Queue<string>();
            queue.Enqueue(key);
            int pushed = 0, guard = 0;
            while (queue.Count > 0 && guard++ < 100_000)
            {
                var pred = _byKey[queue.Dequeue()];
                if (!_successors.TryGetValue(pred.Key, out var next)) continue;
                foreach (var succKey in next)
                {
                    if (!_byKey.TryGetValue(succKey, out var succ) || succ.Blocks.Count == 0) continue;
                    int first = FirstPending(succ);
                    if (first >= succ.Blocks.Count) continue;
                    int deficit = LastDay(pred) + 1 - succ.Blocks[first].Day;
                    if (deficit <= 0) continue;
                    if (first > 0) throw new Rejected("Un prefijo completado impide la cascada.");
                    Shift(succ, first, deficit, direct: false, allowOutside: allowOutside);
                    if (succ.Move != TimelineMoveKind.Direct) succ.Move = TimelineMoveKind.Cascade;
                    pushed++;
                    queue.Enqueue(succKey);
                }
            }
            return pushed;
        }

        /// <summary>Blocks, move kind and resource of every activity.</summary>
        private sealed class Snapshot
        {
            private readonly List<(TimelineRow Row, TimelineBlock[] Blocks, TimelineMoveKind Move, string Resource)> _rows =
                new List<(TimelineRow, TimelineBlock[], TimelineMoveKind, string)>();

            public static Snapshot Take(TimelineModel model)
            {
                var s = new Snapshot();
                foreach (var row in model.Rows)
                    if (row.Kind == TimelineRowKind.Activity) s._rows.Add((row, row.Blocks.ToArray(), row.Move, row.Resource));
                return s;
            }

            public void Restore(TimelineModel model)
            {
                foreach (var (row, blocks, move, resource) in _rows)
                {
                    row.Blocks.Clear();
                    row.Blocks.AddRange(blocks);
                    row.Move = move;
                    row.Resource = resource;
                }
            }
        }
    }
}
