using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using static Maptek.Vulcan.Stp.Engine.EngineException;
using static Maptek.Vulcan.Stp.Engine.Internal.Js;

namespace Maptek.Vulcan.Stp.Engine.Internal
{
    /// <summary>
    /// Line-by-line port of the model rules of planner_logic.js (L160-1076) over the mutable working model.
    /// Every member names its JS function and line range. Inputs are never mutated: like the JS, each rule
    /// clones (<see cref="WModel.Clone"/>) before it changes anything.
    /// </summary>
    internal static class Rules
    {
        private const long Day = CivilTime.DayMicroseconds;
        private static readonly Regex SignedInteger = new Regex(@"^-?[0-9]+\z", RegexOptions.CultureInvariant);
        private static readonly Regex TypeSeparators = new Regex(@"[-_\s]", RegexOptions.CultureInvariant);

        // ------------------------------------------------------------------ fragments & links

        /// <summary>JS: <c>lockedFragment</c>, L523.</summary>
        public static bool Locked(WFragment fragment) => fragment.LockedCompleted;

        /// <summary>JS: <c>activityId</c>, L460.</summary>
        public static string ActivityId(WFragment fragment) => fragment.ActivityId ?? string.Empty;

        /// <summary>JS: <c>normalType</c>, L462-467.</summary>
        public static string NormalType(string value)
        {
            string compact = TypeSeparators.Replace(value ?? string.Empty, string.Empty).ToLowerInvariant();
            switch (compact)
            {
                case "finishstart":
                case "fs":
                    return "FinishStart";
                case "startstart":
                case "ss":
                    return "StartStart";
                case "finishfinish":
                case "ff":
                    return "FinishFinish";
                case "startfinish":
                case "sf":
                    return "StartFinish";
                default:
                    throw Fail($"tipo de precedencia no evaluable: {S(value)}");
            }
        }

        /// <summary>JS: <c>lagUs</c>, L468-471.</summary>
        public static BigInteger LagUs(WLink link)
        {
            if (link.LagMicroseconds == null || !SignedInteger.IsMatch(link.LagMicroseconds))
            {
                throw Fail("lag de precedencia no evaluable");
            }

            return BigInteger.Parse(link.LagMicroseconds, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }

        /// <summary>JS: <c>fragmentsFor</c>, L472-476.</summary>
        public static List<WFragment> FragmentsFor(WModel model, string id, string barKey)
        {
            string bar = S(barKey);
            var found = model.Fragments.Where(f => ActivityId(f) == id && (f.NativeBarKey ?? string.Empty) == bar).ToList();
            if (found.Count == 0)
            {
                throw Fail($"barra de precedencia no evaluable para actividad {id}");
            }

            return found;
        }

        /// <summary>JS: <c>span</c>, L477-482.</summary>
        public static (long Start, long Finish) Span(WModel model, string id, string barKey)
        {
            var fragments = FragmentsFor(model, id, barKey);
            long start = CivilTime.Scalar(fragments[0].Start), finish = CivilTime.Scalar(fragments[0].Finish);
            foreach (var fragment in fragments.Skip(1))
            {
                long s = CivilTime.Scalar(fragment.Start), f = CivilTime.Scalar(fragment.Finish);
                start = start < s ? start : s;
                finish = finish > f ? finish : f;
            }

            return (start, finish);
        }

        /// <summary>Positive precedence deficit in microseconds (0 when satisfied). JS: <c>requiredShift</c>, L483-497.</summary>
        public static BigInteger RequiredShift(WModel model, WLink link)
        {
            string predecessorId = S(link.PredecessorId), successorId = S(link.SuccessorId);
            var predecessor = Span(model, predecessorId, link.PredecessorBar);
            var successor = Span(model, successorId, link.SuccessorBar);
            BigInteger lag = LagUs(link), amount;
            switch (NormalType(link.Type))
            {
                case "FinishStart": amount = predecessor.Finish + lag - successor.Start; break;
                case "StartStart": amount = predecessor.Start + lag - successor.Start; break;
                case "FinishFinish": amount = predecessor.Finish + lag - successor.Finish; break;
                default: amount = predecessor.Start + lag - successor.Finish; break;
            }

            return amount > BigInteger.Zero ? amount : BigInteger.Zero;
        }

        /// <summary>Microseconds rounded up to whole days. JS: <c>ceilDays</c>, L498 (clamped to a long; beyond ±10^7 days any shift is CIVIL_RANGE).</summary>
        public static long CeilDays(BigInteger value)
        {
            BigInteger days = (value + Day - 1) / Day;
            if (days > long.MaxValue)
            {
                return long.MaxValue;
            }

            return days < long.MinValue ? long.MinValue : (long)days;
        }

        // ------------------------------------------------------------------ shifting

        private sealed class Proposed
        {
            public WFragment Fragment;
            public string Start;
            public string Finish;
        }

        /// <summary>Computes every shifted position before anything is applied. JS: <c>validateShiftBounds</c>, L499-501.</summary>
        private static List<Proposed> ValidateShiftBounds(IEnumerable<WFragment> fragments, long days) =>
            fragments.Select(f => new Proposed { Fragment = f, Start = CivilTime.AddDays(f.Start, days), Finish = CivilTime.AddDays(f.Finish, days) }).ToList();

        /// <summary>JS: <c>applyShift</c>, L502-504.</summary>
        /// <remarks>
        /// Deliberate deviation from JS: a shift that lands back on the original instant keeps the original
        /// text. JS re-renders it (<c>addCivilDays</c> trims fractions, so Python's <c>06:30:00.250000</c> comes back
        /// as <c>06:30:00.25</c>), and because "moved" is a text comparison, a fragment moved away and back stayed
        /// <c>direct</c> with no real shift: the server rejects that package (<c>movement_provenance_without_shift</c>).
        /// For whole-second times the re-rendered text already equals the original, so this matches JS exactly.
        /// </remarks>
        private static void ApplyShift(List<Proposed> proposed, string provenance)
        {
            foreach (var item in proposed)
            {
                item.Fragment.Start = SameInstantText(item.Start, item.Fragment.OriginalStart);
                item.Fragment.Finish = SameInstantText(item.Finish, item.Fragment.OriginalFinish);
                item.Fragment.Provenance = provenance;
            }
        }

        private static string SameInstantText(string shifted, string original)
        {
            if (shifted == original || original == null)
            {
                return shifted;
            }

            try
            {
                return CivilTime.Scalar(shifted) == CivilTime.Scalar(original) ? original : shifted;
            }
            catch (EngineException)
            {
                return shifted;
            }
        }

        private static int ByStart(WFragment a, WFragment b) => Cmp(CivilTime.Scalar(a.Start), CivilTime.Scalar(b.Start));

        private static int ByOriginalStart(WFragment a, WFragment b) => Cmp(CivilTime.Scalar(a.OriginalStart), CivilTime.Scalar(b.OriginalStart));

        /// <summary>Cascade shift of the pending, non-direct fragments of an activity. JS: <c>shiftFragments</c>, L505-522.</summary>
        public static void ShiftFragments(WModel model, string id, long days, string provenance)
        {
            var relevant = SortStable(model.Fragments.Where(f => ActivityId(f) == id), ByStart);
            if (relevant.Count == 0)
            {
                throw Fail($"actividad desconocida: {id}");
            }

            var pending = PendingSuffix(relevant, id);
            var movable = pending.Where(f => f.Provenance != "direct").ToList();
            if (movable.Count == 0)
            {
                throw Fail($"sin tramo pendiente para cascada: {id}");
            }

            ApplyShift(ValidateShiftBounds(movable, days), provenance);
        }

        private static List<WFragment> PendingSuffix(List<WFragment> relevant, string id)
        {
            bool sawPending = false;
            var pending = new List<WFragment>();
            foreach (var fragment in relevant)
            {
                if (Locked(fragment))
                {
                    if (sawPending)
                    {
                        throw Fail($"prefijo completado ambiguo: {id}");
                    }

                    continue;
                }

                sawPending = true;
                pending.Add(fragment);
            }

            if (pending.Count == 0)
            {
                throw Fail($"actividad completada: {id}");
            }

            return pending;
        }

        /// <summary>Pending fragments after the completed prefix, by original start. JS: <c>pendingFragments</c>, L524-540.</summary>
        public static List<WFragment> PendingFragments(WModel model, string id)
        {
            var relevant = SortStable(model.Fragments.Where(f => ActivityId(f) == id), ByOriginalStart);
            if (relevant.Count == 0)
            {
                throw Fail($"actividad desconocida: {id}");
            }

            return PendingSuffix(relevant, id);
        }

        /// <summary>Uniform whole-day shift of a fragment. JS: <c>shiftDays</c>, L541-548.</summary>
        public static long ShiftDays(WFragment fragment)
        {
            long start = CivilTime.Scalar(fragment.Start) - CivilTime.Scalar(fragment.OriginalStart);
            long finish = CivilTime.Scalar(fragment.Finish) - CivilTime.Scalar(fragment.OriginalFinish);
            if (start != finish || start % Day != 0)
            {
                throw Fail($"movimiento civil inválido: {S(fragment.Id)}");
            }

            return start / Day;
        }

        // ------------------------------------------------------------------ logical units

        private static int OriginalOrder(WFragment left, WFragment right)
        {
            long start = CivilTime.Scalar(left.OriginalStart) - CivilTime.Scalar(right.OriginalStart);
            if (start != 0)
            {
                return start < 0 ? -1 : 1;
            }

            long finish = CivilTime.Scalar(left.OriginalFinish) - CivilTime.Scalar(right.OriginalFinish);
            return finish == 0 ? JsCompare.LocaleCompare(left.Id, right.Id) : (finish < 0 ? -1 : 1);
        }

        private static int CurrentOrder(WFragment left, WFragment right)
        {
            long start = CivilTime.Scalar(left.Start) - CivilTime.Scalar(right.Start);
            if (start != 0)
            {
                return start < 0 ? -1 : 1;
            }

            long finish = CivilTime.Scalar(left.Finish) - CivilTime.Scalar(right.Finish);
            return finish == 0 ? JsCompare.LocaleCompare(left.Id, right.Id) : (finish < 0 ? -1 : 1);
        }

        /// <summary>
        /// Groups an activity's fragments into civil-day units and checks split integrity
        /// (uniform shift, order and joins). JS: <c>logicalUnits</c>, L549-594.
        /// </summary>
        public static List<LogicalUnit> LogicalUnits(IEnumerable<WFragment> fragments, string id)
        {
            var grouped = new Dictionary<string, List<WFragment>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var fragment in fragments)
            {
                if (ActivityId(fragment) != id)
                {
                    continue;
                }

                string key = !string.IsNullOrEmpty(fragment.LogicalUnitDate) ? fragment.LogicalUnitDate : CivilTime.Date(fragment.OriginalStart);
                if (!grouped.TryGetValue(key, out var list))
                {
                    grouped[key] = list = new List<WFragment>();
                    order.Add(key);
                }

                list.Add(fragment);
            }

            var units = new List<LogicalUnit>();
            foreach (string originalDate in SortStable(order, JsCompare.LocaleCompare))
            {
                var originalOrder = SortStable(grouped[originalDate], OriginalOrder);
                var shifts = originalOrder.Select(fragment =>
                {
                    long start = CivilTime.Scalar(fragment.Start) - CivilTime.Scalar(fragment.OriginalStart);
                    long finish = CivilTime.Scalar(fragment.Finish) - CivilTime.Scalar(fragment.OriginalFinish);
                    if (start != finish)
                    {
                        throw Fail($"split nativo deformado en actividad {id}");
                    }

                    return start;
                }).ToList();
                if (shifts.Any(amount => amount != shifts[0]))
                {
                    throw Fail($"split nativo separado en actividad {id}");
                }

                var currentOrder = SortStable(originalOrder, CurrentOrder);
                for (int index = 0; index < currentOrder.Count; index++)
                {
                    if (currentOrder[index].Id != originalOrder[index].Id)
                    {
                        throw Fail($"orden de split nativo alterado en actividad {id}");
                    }
                }

                var first = originalOrder[0];
                foreach (var fragment in originalOrder)
                {
                    if (CivilTime.Scalar(fragment.Start) - CivilTime.Scalar(first.Start) != CivilTime.Scalar(fragment.OriginalStart) - CivilTime.Scalar(first.OriginalStart)
                        || CivilTime.Scalar(fragment.Finish) - CivilTime.Scalar(first.Finish) != CivilTime.Scalar(fragment.OriginalFinish) - CivilTime.Scalar(first.OriginalFinish))
                    {
                        throw Fail($"unión de split nativo alterada en actividad {id}");
                    }
                }

                units.Add(new LogicalUnit
                {
                    Id = originalDate,
                    OriginalDate = originalDate,
                    Fragments = originalOrder,
                    CurrentDate = !string.IsNullOrEmpty(first.LogicalUnitDate)
                        ? CivilTime.AddDays(originalDate, ShiftDays(first))
                        : CivilTime.Date(first.Start),
                });
            }

            return units;
        }

        /// <summary>JS: <c>logicalUnits(model, id)</c>.</summary>
        public static List<LogicalUnit> LogicalUnits(WModel model, string id) => LogicalUnits(model.Fragments, id);

        /// <summary>Moves a unit to a target date. JS: <c>shiftLogicalUnit</c>, L595-600 (unused by the JS module itself).</summary>
        public static void ShiftLogicalUnit(LogicalUnit unit, string targetDate, string provenance)
        {
            CivilParts source = CivilTime.Parse(unit.CurrentDate), target = CivilTime.Parse(targetDate);
            long days = CivilTime.DayNumber(target.Year, target.Month, target.Day) - CivilTime.DayNumber(source.Year, source.Month, source.Day);
            ApplyShift(ValidateShiftBounds(unit.Fragments, days), provenance);
            unit.CurrentDate = targetDate;
        }

        /// <summary>JS: <c>shiftLogicalUnitByDays</c>, L601-610.</summary>
        public static void ShiftLogicalUnitByDays(LogicalUnit unit, long days, string provenance)
        {
            if (!IsSafeInteger(days) || days == 0)
            {
                throw Fail("movimiento civil inválido");
            }

            var proposed = unit.Fragments.Select(f => new Proposed { Fragment = f, Start = CivilTime.AddDays(f.Start, days), Finish = CivilTime.AddDays(f.Finish, days) }).ToList();
            ApplyShift(proposed, provenance);
            unit.CurrentDate = CivilTime.AddDays(unit.CurrentDate, days);
        }

        // ------------------------------------------------------------------ requests & topology

        private static readonly Regex DateOnly = new Regex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant);

        /// <summary>JS: <c>validateDailyTailMoveRequest</c>, L611-621.</summary>
        public static DailyTailMoveRequest ValidateDailyTailMoveRequest(DailyTailMoveRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.ActivityId) || string.IsNullOrEmpty(request.FragmentId)
                || request.TargetDate == null || !DateOnly.IsMatch(request.TargetDate))
            {
                throw Fail("solicitud de inserción diaria inválida");
            }

            CivilTime.Parse(request.TargetDate);
            return request;
        }

        /// <summary>JS: <c>validateDailyStaggerRequest</c>, L622-632.</summary>
        public static DailyStaggerRequest ValidateDailyStaggerRequest(DailyStaggerRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.ActivityId) || string.IsNullOrEmpty(request.FragmentId)
                || request.Divisions < 1 || request.GapDays < 1)
            {
                throw Fail("solicitud de dividir y desfasar inválida");
            }

            return request;
        }

        /// <summary>Ids, keys, originals, quantities, anchors and split order must be unchanged. JS: <c>validateNativeTopology</c>, L633-648.</summary>
        public static void ValidateNativeTopology(WModel reference, WModel candidate)
        {
            if (reference.Fragments.Count != candidate.Fragments.Count)
            {
                throw Fail("topología nativa alterada");
            }

            var byId = new Dictionary<string, WFragment>(StringComparer.Ordinal);
            foreach (var fragment in candidate.Fragments)
            {
                byId[S(fragment.Id)] = fragment;
            }

            foreach (var fragment in reference.Fragments)
            {
                if (!byId.TryGetValue(S(fragment.Id), out var current) || current.ActivityId != fragment.ActivityId || current.NativeBarKey != fragment.NativeBarKey
                    || current.NativeBarXmlKey != fragment.NativeBarXmlKey || current.OriginalStart != fragment.OriginalStart
                    || current.OriginalFinish != fragment.OriginalFinish || current.QuantityPercentage != fragment.QuantityPercentage
                    || (current.AnchorBefore ?? string.Empty) != (fragment.AnchorBefore ?? string.Empty)
                    || (current.AnchorAfter ?? string.Empty) != (fragment.AnchorAfter ?? string.Empty)
                    || current.SplitOrder != fragment.SplitOrder)
                {
                    throw Fail($"topología nativa alterada: {S(fragment.Id)}");
                }
            }

            foreach (var activity in candidate.Activities)
            {
                LogicalUnits(candidate, S(activity.Id));
            }
        }

        // ------------------------------------------------------------------ direct state

        /// <summary>Uniform day shift per directly moved activity, in ordinal id order. JS: <c>existingDirectShifts</c>, L649-665.</summary>
        public static List<KeyValuePair<string, long>> ExistingDirectShifts(WModel model)
        {
            var directIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fragment in model.Fragments)
            {
                if (fragment.Provenance != "direct")
                {
                    continue;
                }

                if (Locked(fragment))
                {
                    throw Fail($"fragmento completado directo: {S(fragment.Id)}");
                }

                directIds.Add(ActivityId(fragment));
            }

            var totals = new List<KeyValuePair<string, long>>();
            foreach (string id in SortStable(directIds, JsCompare.Ordinal))
            {
                var pending = PendingFragments(model, id);
                if (pending.Any(f => f.Provenance != "direct"))
                {
                    throw Fail($"cobertura directa incompleta: {id}");
                }

                var shifts = pending.Select(ShiftDays).ToList();
                if (shifts.Count == 0 || shifts[0] == 0 || shifts.Any(days => days != shifts[0]))
                {
                    throw Fail($"movimiento directo no uniforme: {id}");
                }

                totals.Add(new KeyValuePair<string, long>(id, shifts[0]));
            }

            return totals;
        }

        /// <summary>Activities with direct fragments (insertion order); each unit is all-direct or not at all. JS: <c>directActivityIds</c>, L666-680.</summary>
        public static List<string> DirectActivityIds(WModel model)
        {
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fragment in model.Fragments)
            {
                if (fragment.Provenance != "direct")
                {
                    continue;
                }

                if (Locked(fragment))
                {
                    throw Fail($"fragmento completado directo: {S(fragment.Id)}");
                }

                if (seen.Add(ActivityId(fragment)))
                {
                    ids.Add(ActivityId(fragment));
                }
            }

            foreach (string id in ids)
            {
                foreach (var unit in LogicalUnits(model, id))
                {
                    int direct = unit.Fragments.Count(f => f.Provenance == "direct");
                    if (direct != 0 && direct != unit.Fragments.Count)
                    {
                        throw Fail($"cobertura directa incompleta de unidad: {id}");
                    }
                }
            }

            return ids;
        }

        /// <summary>JS: <c>restoreCurrentFragments</c>, L681-690.</summary>
        public static void RestoreCurrentFragments(WModel rebuilt, WModel source, Func<WFragment, bool> include)
        {
            var byId = new Dictionary<string, WFragment>(StringComparer.Ordinal);
            foreach (var fragment in rebuilt.Fragments)
            {
                byId[S(fragment.Id)] = fragment;
            }

            foreach (var fragment in source.Fragments)
            {
                if (!include(fragment))
                {
                    continue;
                }

                var target = byId[S(fragment.Id)];
                target.Start = fragment.Start;
                target.Finish = fragment.Finish;
                target.Provenance = fragment.Provenance;
            }
        }

        /// <summary>
        /// Rebuilds a preview from the direct fragments only, then re-cascades. JS: <c>rebuildDirectPreview</c>, L691-702.
        /// </summary>
        public static WModel RebuildDirectPreview(WModel model)
        {
            var direct = DirectActivityIds(model);
            var rebuilt = ResetPendingToOriginal(model);
            RestoreCurrentFragments(rebuilt, model, f => f.Provenance == "direct");
            CheckOverlaps(rebuilt);

            // A re-edited cascaded tail may leave earlier units of that successor temporarily at their original
            // dates. Recompute their permitted cascade first (JS comment L695-698).
            var cascaded = CascadeForward(rebuilt, direct);
            IncomingSafe(cascaded, new HashSet<string>(direct, StringComparer.Ordinal));
            return cascaded;
        }

        /// <summary>True when a direct activity is partly direct or shifted non-uniformly (tail moves). JS: <c>hasNonUniformDirectState</c>, L703-711.</summary>
        public static bool HasNonUniformDirectState(WModel model)
        {
            foreach (string id in DirectActivityIds(model))
            {
                var pending = PendingFragments(model, id);
                int direct = pending.Count(f => f.Provenance == "direct");
                if (direct != pending.Count)
                {
                    return true;
                }

                var shifts = pending.Select(ShiftDays).ToList();
                if (shifts.Any(days => days != shifts[0]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>JS: <c>resetPendingToOriginal</c>, L712-721.</summary>
        public static WModel ResetPendingToOriginal(WModel model)
        {
            var rebuilt = model.Clone();
            foreach (var fragment in rebuilt.Fragments)
            {
                if (Locked(fragment))
                {
                    continue;
                }

                fragment.Start = fragment.OriginalStart;
                fragment.Finish = fragment.OriginalFinish;
                fragment.Provenance = string.Empty;
            }

            return rebuilt;
        }

        /// <summary>JS: <c>requireDirectContainment</c>, L722-729.</summary>
        public static void RequireDirectContainment(IEnumerable<WFragment> pending, CivilHorizon horizon, long days)
        {
            long lower = CivilTime.Scalar(horizon.HorizonStart), upper = CivilTime.Scalar(horizon.HorizonFinish);
            foreach (var fragment in pending)
            {
                long originalStart = CivilTime.Scalar(fragment.OriginalStart), originalFinish = CivilTime.Scalar(fragment.OriginalFinish);
                long start = CivilTime.Scalar(CivilTime.AddDays(fragment.OriginalStart, days)), finish = CivilTime.Scalar(CivilTime.AddDays(fragment.OriginalFinish, days));
                if (originalStart < lower || originalFinish > upper || start < lower || finish > upper)
                {
                    throw Fail($"movimiento directo fuera del horizonte: {S(fragment.Id)}", HorizonRange);
                }
            }
        }

        /// <summary>JS: <c>requireCurrentContainment</c>, L730-735.</summary>
        public static void RequireCurrentContainment(IEnumerable<WFragment> fragments, CivilHorizon horizon)
        {
            long lower = CivilTime.Scalar(horizon.HorizonStart), upper = CivilTime.Scalar(horizon.HorizonFinish);
            foreach (var fragment in fragments)
            {
                if (CivilTime.Scalar(fragment.Start) < lower || CivilTime.Scalar(fragment.Finish) > upper)
                {
                    throw Fail($"movimiento directo fuera del horizonte: {S(fragment.Id)}", HorizonRange);
                }
            }
        }

        /// <summary>No overlapping fragments and at most one logical unit per civil day, per activity. JS: <c>checkOverlaps</c>, L736-747.</summary>
        public static void CheckOverlaps(WModel model)
        {
            foreach (var activity in model.Activities)
            {
                string id = activity.Id ?? string.Empty;
                var ordered = SortStable(model.Fragments.Where(f => ActivityId(f) == id), ByStart);
                for (int index = 1; index < ordered.Count; index++)
                {
                    if (CivilTime.Scalar(ordered[index - 1].Finish) > CivilTime.Scalar(ordered[index].Start))
                    {
                        throw Fail($"solapamiento de fragmentos en actividad {id}");
                    }
                }

                var occupied = new HashSet<string>(StringComparer.Ordinal);
                foreach (var unit in LogicalUnits(model, id))
                {
                    if (!occupied.Add(unit.CurrentDate))
                    {
                        throw Fail($"ocupación civil duplicada en actividad {id}: dos unidades en el mismo día");
                    }
                }
            }
        }

        /// <summary>A directly moved successor may not worsen its incoming precedence deficit. JS: <c>incomingSafe</c>, L748-760.</summary>
        public static void IncomingSafe(WModel model, ISet<string> directIds)
        {
            var baselineDeficits = BaselinePrecedenceDeficits(model);
            for (int index = 0; index < model.Precedences.Count; index++)
            {
                var link = model.Precedences[index];
                string successor = S(link.SuccessorId);
                bool endpointHasDirectMovement = FragmentsFor(model, successor, link.SuccessorBar).Any(f => f.Provenance == "direct");
                if (directIds.Contains(successor) && endpointHasDirectMovement && RequiredShift(model, link) > baselineDeficits[index])
                {
                    throw Fail($"precedencia entrante agravada para actividad {successor}");
                }
            }
        }

        /// <summary>JS: <c>originalSchedule</c>, L762-770.</summary>
        public static WModel OriginalSchedule(WModel model)
        {
            var baseline = model.Clone();
            foreach (var fragment in baseline.Fragments)
            {
                fragment.Start = fragment.OriginalStart;
                fragment.Finish = fragment.OriginalFinish;
                fragment.Provenance = string.Empty;
            }

            return baseline;
        }

        /// <summary>Deficit of every link in the native schedule. JS: <c>baselinePrecedenceDeficits</c>, L772-775.</summary>
        public static List<BigInteger> BaselinePrecedenceDeficits(WModel model)
        {
            var baseline = OriginalSchedule(model);
            return baseline.Precedences.Select(link => RequiredShift(baseline, link)).ToList();
        }

        /// <summary>JS: <c>ensurePrecedencesNotWorsened</c>, L777-783.</summary>
        public static void EnsurePrecedencesNotWorsened(WModel model, List<BigInteger> baselineDeficits)
        {
            for (int index = 0; index < model.Precedences.Count; index++)
            {
                if (RequiredShift(model, model.Precedences[index]) > baselineDeficits[index])
                {
                    throw Fail($"precedencia preexistente agravada: {model.Precedences[index].Key ?? string.Empty}");
                }
            }
        }

        // ------------------------------------------------------------------ normalization & preflight

        /// <summary>
        /// JS: <c>normalizeModel</c>, L438-459 (with <c>normalizeLag</c>, L419-437). The typed model has no JSON aliases
        /// (<c>activity_id</c>, <c>locked_completed</c>, ...): mapping them, and rejecting conflicting aliases, belongs to the
        /// wire-format layer. What remains is the logical-unit date check and the lag normalization.
        /// </summary>
        public static WModel NormalizeModel(WModel model)
        {
            var normalized = (model ?? new WModel()).Clone();
            foreach (var fragment in normalized.Fragments)
            {
                if (fragment.LogicalUnitDate != null && CivilTime.Date(fragment.LogicalUnitDate) != fragment.LogicalUnitDate)
                {
                    throw Fail("unidad lógica inválida");
                }
            }

            foreach (var link in normalized.Precedences)
            {
                NormalizeLag(link);
            }

            return normalized;
        }

        /// <summary>JS: <c>normalizeLag</c>, L419-437.</summary>
        private static void NormalizeLag(WLink link)
        {
            var supplied = new List<BigInteger>();
            if (link.LagMicroseconds != null)
            {
                if (!SignedInteger.IsMatch(link.LagMicroseconds))
                {
                    throw Fail("lag de precedencia inválido");
                }

                supplied.Add(BigInteger.Parse(link.LagMicroseconds, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
            }

            if (link.LagSeconds.HasValue)
            {
                if (!IsSafeInteger(link.LagSeconds.Value))
                {
                    throw Fail("lag de precedencia inválido");
                }

                supplied.Add(new BigInteger(link.LagSeconds.Value) * 1_000_000);
            }

            if (supplied.Count > 0 && supplied.Any(value => value != supplied[0]))
            {
                throw Fail("lag de precedencia conflictivo");
            }

            link.LagMicroseconds = (supplied.Count > 0 ? supplied[0] : BigInteger.Zero).ToString(CultureInfo.InvariantCulture);
            link.LagSeconds = null;
        }

        /// <summary>JS: <c>validatePlannerStructure</c>, L785-824.</summary>
        public static void ValidatePlannerStructure(WModel model, bool strictBrowser)
        {
            var activityBars = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var nativeBarCatalogs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var activity in model.Activities)
            {
                if (string.IsNullOrEmpty(activity.Id))
                {
                    throw Fail("actividad inválida");
                }

                if (activityBars.ContainsKey(activity.Id))
                {
                    throw Fail($"actividad duplicada: {activity.Id}");
                }

                if (strictBrowser && activity.NativeBars == null)
                {
                    throw Fail($"barras nativas inválidas para actividad {activity.Id}");
                }

                if (activity.NativeBars != null)
                {
                    nativeBarCatalogs.Add(activity.Id);
                }

                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var bar in activity.NativeBars ?? new List<NativeBar>())
                {
                    if (bar == null || string.IsNullOrEmpty(bar.Key))
                    {
                        throw Fail($"barra nativa inválida para actividad {activity.Id}");
                    }

                    if (!keys.Add(bar.Key))
                    {
                        throw Fail($"barra nativa duplicada para actividad {activity.Id}");
                    }
                }

                activityBars[activity.Id] = keys;
            }

            var fragmentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fragment in model.Fragments)
            {
                if (string.IsNullOrEmpty(fragment.Id))
                {
                    throw Fail("fragmento inválido");
                }

                if (!fragmentIds.Add(fragment.Id))
                {
                    throw Fail($"fragmento duplicado: {fragment.Id}");
                }

                if (fragment.ActivityId == null || !activityBars.ContainsKey(fragment.ActivityId))
                {
                    throw Fail($"propiedad de fragmento inválida: {fragment.Id}");
                }

                if (string.IsNullOrEmpty(fragment.NativeBarKey))
                {
                    throw Fail($"barra nativa inválida: {fragment.Id}");
                }

                var bars = activityBars[fragment.ActivityId];
                if ((strictBrowser || nativeBarCatalogs.Contains(fragment.ActivityId)) && !bars.Contains(fragment.NativeBarKey))
                {
                    throw Fail($"barra nativa desconocida: {fragment.Id}");
                }

                long originalStart = 0, originalFinish = 0;
                if (strictBrowser)
                {
                    try
                    {
                        originalStart = CivilTime.Scalar(fragment.OriginalStart);
                        originalFinish = CivilTime.Scalar(fragment.OriginalFinish);
                    }
                    catch (EngineException)
                    {
                        throw Fail($"fragmento sin estado original verificable: {fragment.Id}");
                    }
                }

                foreach (bool before in new[] { true, false })
                {
                    string value = before ? fragment.AnchorBefore : fragment.AnchorAfter;
                    if (string.IsNullOrEmpty(value))
                    {
                        continue;
                    }

                    long anchor;
                    try
                    {
                        anchor = CivilTime.Scalar(value);
                    }
                    catch (EngineException)
                    {
                        throw Fail($"ancla nativa inválida: {fragment.Id}");
                    }

                    if (strictBrowser && ((before && anchor > originalStart) || (!before && anchor < originalFinish)))
                    {
                        throw Fail($"ancla nativa cruzada: {fragment.Id}");
                    }
                }
            }
        }

        /// <summary>Normalizes and validates; locked fragments must sit at their originals. JS: <c>preflight</c>, L826-840.</summary>
        public static WModel Preflight(WModel model, bool strictBrowser = false)
        {
            var normalized = NormalizeModel(model);
            var baseline = normalized.Clone();
            ValidatePlannerStructure(normalized, strictBrowser);
            foreach (var fragment in baseline.Fragments)
            {
                if (fragment.OriginalStart == null || fragment.OriginalFinish == null)
                {
                    throw Fail($"fragmento sin estado original verificable: {S(fragment.Id)}");
                }

                if (Locked(fragment) && (fragment.Start != fragment.OriginalStart || fragment.Finish != fragment.OriginalFinish))
                {
                    throw Fail($"fragmento completado modificado: {S(fragment.Id)}");
                }

                fragment.Start = fragment.OriginalStart;
                fragment.Finish = fragment.OriginalFinish;
            }

            foreach (var link in baseline.Precedences)
            {
                NormalType(link.Type);
                LagUs(link);
            }

            return normalized;
        }

        // ------------------------------------------------------------------ horizon

        /// <summary>JS: <c>durationDays</c>, L57-60.</summary>
        public static int DurationDays(long value)
        {
            if (value < 1 || value > 366)
            {
                throw Fail("duración de horizonte inválida");
            }

            return (int)value;
        }

        /// <summary>JS: <c>deriveCivilHorizon</c>, L84-94.</summary>
        public static CivilHorizon DeriveCivilHorizon(WModel model, long durationValue)
        {
            int duration = DurationDays(durationValue);
            var normalized = NormalizeModel(model);
            var pending = normalized.Fragments.Where(f => !Locked(f)).ToList();
            if (pending.Count == 0)
            {
                throw Fail("no hay trabajo pendiente para planificar");
            }

            string earliest = pending[0].OriginalStart;
            foreach (var fragment in pending.Skip(1))
            {
                if (CivilTime.Scalar(fragment.OriginalStart) < CivilTime.Scalar(earliest))
                {
                    earliest = fragment.OriginalStart;
                }
            }

            CivilParts part = CivilTime.Parse(earliest);
            string horizonStart = CivilTime.DateText(part.Year, part.Month, part.Day) + "T00:00:00";
            return new CivilHorizon { HorizonStart = horizonStart, HorizonFinish = CivilTime.AddDays(horizonStart, duration), DurationDays = duration };
        }

        /// <summary>JS: <c>validateCivilHorizon</c>, L96-114.</summary>
        public static CivilHorizon ValidateCivilHorizon(WModel model, CivilHorizon horizon)
        {
            if (horizon == null)
            {
                throw Fail("horizonte civil inválido");
            }

            string horizonStart, horizonFinish;
            try
            {
                horizonStart = CivilTime.MidnightText(horizon.HorizonStart);
                horizonFinish = CivilTime.MidnightText(horizon.HorizonFinish);
            }
            catch (EngineException)
            {
                throw Fail("horizonte civil inválido");
            }

            long duration = CivilTime.DayCount(horizonStart, horizonFinish);
            if (duration < 1 || duration > 366)
            {
                throw Fail("horizonte civil inválido");
            }

            var normalized = NormalizeModel(model);
            long lower = CivilTime.Scalar(horizonStart), upper = CivilTime.Scalar(horizonFinish);
            if (!normalized.Fragments.Any(fragment =>
            {
                if (Locked(fragment))
                {
                    return false;
                }

                string start = fragment.Start ?? fragment.OriginalStart;
                string finish = fragment.Finish ?? fragment.OriginalFinish;
                return CivilTime.Scalar(finish) > lower && CivilTime.Scalar(start) < upper;
            }))
            {
                throw Fail("horizonte sin trabajo pendiente");
            }

            return new CivilHorizon { HorizonStart = horizonStart, HorizonFinish = horizonFinish, DurationDays = (int)duration };
        }

        // ------------------------------------------------------------------ public operations

        /// <summary>JS: <c>preexistingPrecedenceConflicts</c>, L190-198.</summary>
        public static List<PrecedenceConflict> PreexistingPrecedenceConflicts(WModel model)
        {
            var normalized = Preflight(model, true);
            var baseline = OriginalSchedule(normalized);
            return baseline.Precedences.Where(link => RequiredShift(baseline, link) > 0).Select(link => new PrecedenceConflict
            {
                Key = link.Key ?? string.Empty,
                PredecessorId = S(link.PredecessorId),
                SuccessorId = S(link.SuccessorId),
            }).ToList();
        }

        /// <summary>
        /// Breadth-first push of successors by the deficit above the baseline deficit, rounded up to whole days,
        /// with a cycle guard. JS: <c>cascadeForward</c>, L842-868.
        /// </summary>
        public static WModel CascadeForward(WModel model, IEnumerable<string> directActivityIds)
        {
            var normalized = Preflight(model);
            var result = normalized.Clone();
            var queue = SortStable(new HashSet<string>(directActivityIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal), JsCompare.Ordinal);
            var baselineDeficits = BaselinePrecedenceDeficits(normalized);
            int cursor = 0;
            long moves = 0, limit = Math.Max(1L, result.Fragments.Count * (long)Math.Max(1, result.Precedences.Count));
            while (cursor < queue.Count)
            {
                string current = queue[cursor++];
                for (int index = 0; index < result.Precedences.Count; index++)
                {
                    var link = result.Precedences[index];
                    string predecessor = S(link.PredecessorId), successor = S(link.SuccessorId);
                    if (predecessor != current)
                    {
                        continue;
                    }

                    BigInteger needed = RequiredShift(result, link) - baselineDeficits[index];
                    if (needed <= 0)
                    {
                        continue;
                    }

                    var successorFragments = FragmentsFor(result, successor, link.SuccessorBar);
                    if (successorFragments.All(f => f.Provenance == "direct"))
                    {
                        throw Fail($"conflicto entre movimiento directo y cascada para {successor}");
                    }

                    if (successorFragments.Any(Locked))
                    {
                        throw Fail($"prefijo completado impide la cascada para {successor}");
                    }

                    ShiftFragments(result, successor, CeilDays(needed), "cascade");
                    queue.Add(successor);
                    moves += 1;
                    if (moves > limit)
                    {
                        throw Fail("ciclo o no convergencia de precedencias");
                    }
                }
            }

            CheckOverlaps(result);
            EnsurePrecedencesNotWorsened(result, baselineDeficits);
            return result;
        }

        /// <summary>Moves a fragment and the rest of its bar to a new start. JS: <c>moveFragment</c>, L870-883.</summary>
        public static WModel MoveFragment(WModel model, string fragmentId, string newStart)
        {
            var normalized = Preflight(model);
            var result = normalized.Clone();
            var target = result.Fragments.FirstOrDefault(f => f.Id == fragmentId);
            if (target == null)
            {
                throw Fail($"fragmento desconocido: {S(fragmentId)}");
            }

            if (Locked(target))
            {
                throw Fail("fragmento completado no se puede mover");
            }

            long delta = CivilTime.Scalar(newStart) - CivilTime.Scalar(target.Start);
            if (delta % Day != 0)
            {
                throw Fail("el movimiento debe ser en días civiles enteros");
            }

            long days = delta / Day;
            string id = ActivityId(target), key = target.NativeBarKey ?? string.Empty;
            long targetStart = CivilTime.Scalar(target.Start);
            var candidates = SortStable(
                result.Fragments.Where(f => ActivityId(f) == id && (f.NativeBarKey ?? string.Empty) == key && CivilTime.Scalar(f.Start) >= targetStart),
                ByStart);
            if (candidates.Count == 0 || candidates.Any(Locked))
            {
                throw Fail("avance completado impide el movimiento");
            }

            ApplyShift(ValidateShiftBounds(candidates, days), "direct");
            CheckOverlaps(result);
            IncomingSafe(result, new HashSet<string>(StringComparer.Ordinal) { id });
            return days > 0 ? CascadeForward(result, new[] { id }) : result;
        }

        /// <summary>
        /// Moves one civil-day unit and every later unit of the activity to a target date, then rebuilds the preview.
        /// JS: <c>moveDailyTail</c>, L885-905.
        /// </summary>
        public static WModel MoveDailyTail(WModel model, DailyTailMoveRequest requestValue)
        {
            var request = ValidateDailyTailMoveRequest(requestValue);
            var normalized = Preflight(model);
            var result = normalized.Clone();
            var target = result.Fragments.FirstOrDefault(f => f.Id == request.FragmentId);
            if (target == null)
            {
                throw Fail($"fragmento desconocido: {request.FragmentId}");
            }

            if (ActivityId(target) != request.ActivityId)
            {
                throw Fail($"fragmento ajeno a actividad {request.ActivityId}");
            }

            CheckOverlaps(result);
            var units = LogicalUnits(result, request.ActivityId);
            var selected = units.FirstOrDefault(unit => unit.Fragments.Any(f => f.Id == request.FragmentId));
            if (selected == null)
            {
                throw Fail($"unidad diaria desconocida: {request.FragmentId}");
            }

            if (selected.Fragments.Any(Locked))
            {
                throw Fail("fragmento completado no se puede mover");
            }

            if (selected.CurrentDate == request.TargetDate)
            {
                throw Fail("la unidad diaria ya ocupa la fecha destino");
            }

            CivilParts source = CivilTime.Parse(selected.CurrentDate), targetParts = CivilTime.Parse(request.TargetDate);
            long days = CivilTime.DayNumber(targetParts.Year, targetParts.Month, targetParts.Day) - CivilTime.DayNumber(source.Year, source.Month, source.Day);
            string selectedDate = selected.CurrentDate;
            var tail = units.Where(unit => string.CompareOrdinal(unit.CurrentDate, selectedDate) >= 0).ToList();
            if (tail.Any(unit => unit.Fragments.Any(Locked)))
            {
                throw Fail("avance completado impide desplazar la cola diaria");
            }

            foreach (var unit in tail)
            {
                ShiftLogicalUnitByDays(unit, days, "direct");
            }

            CheckOverlaps(result);
            var cascaded = RebuildDirectPreview(result);
            ValidateNativeTopology(normalized, cascaded);
            cascaded.Precedences = (model ?? new WModel()).Precedences.Select(link => link.Clone()).ToList();
            return cascaded;
        }

        /// <summary>
        /// Inserts <c>divisions</c> gaps of <c>gapDays</c> after the selected unit, one daily-tail move per division.
        /// JS: <c>staggerDailyTail</c>, L907-936.
        /// </summary>
        public static WModel StaggerDailyTail(WModel model, DailyStaggerRequest requestValue, CivilHorizon horizonValue)
        {
            var request = ValidateDailyStaggerRequest(requestValue);
            var horizon = horizonValue != null ? ValidateCivilHorizon(model, horizonValue) : null;
            var candidate = Preflight(model);
            var units = LogicalUnits(candidate, request.ActivityId);
            int selectedIndex = units.FindIndex(unit => unit.Fragments.Any(f => f.Id == request.FragmentId));
            if (selectedIndex < 0)
            {
                throw Fail($"unidad diaria desconocida: {request.FragmentId}");
            }

            if (selectedIndex + request.Divisions >= units.Count)
            {
                throw Fail("no hay suficientes unidades pendientes para dividir y desfasar");
            }

            for (int offset = 1; offset <= request.Divisions; offset += 1)
            {
                units = LogicalUnits(candidate, request.ActivityId);
                var next = selectedIndex + offset < units.Count ? units[selectedIndex + offset] : null;
                if (next == null || next.Fragments.Any(Locked))
                {
                    throw Fail("avance completado impide dividir y desfasar");
                }

                string targetDate = CivilTime.AddDays(next.CurrentDate + "T00:00:00", request.GapDays).Substring(0, 10);
                if (horizon != null && (CivilTime.Scalar(targetDate + "T00:00:00") < CivilTime.Scalar(horizon.HorizonStart)
                    || CivilTime.Scalar(targetDate + "T00:00:00") >= CivilTime.Scalar(horizon.HorizonFinish)))
                {
                    throw Fail("fecha destino fuera del horizonte visible", HorizonRange);
                }

                candidate = MoveDailyTail(candidate, new DailyTailMoveRequest
                {
                    ActivityId = request.ActivityId,
                    FragmentId = next.Fragments[0].Id,
                    TargetDate = targetDate,
                });
            }

            return candidate;
        }

        /// <summary>
        /// Whole-activity day shift: accumulates per-activity direct totals, rebuilds from the originals, enforces horizon
        /// containment and incoming precedence, then cascades. JS: <c>moveActivity</c>, L938-967.
        /// </summary>
        public static WModel MoveActivity(WModel model, string idValue, long deltaDays, CivilHorizon horizonValue)
        {
            var normalized = Preflight(model);
            if (deltaDays == 0 || !IsSafeInteger(deltaDays))
            {
                throw Fail("movimiento civil inválido");
            }

            var horizon = ValidateCivilHorizon(normalized, horizonValue);
            string id = S(idValue);
            if (HasNonUniformDirectState(normalized))
            {
                var result = ResetPendingToOriginal(normalized);
                RestoreCurrentFragments(result, normalized, f => f.Provenance == "direct" || ActivityId(f) == id);
                var pending = PendingFragments(result, id);
                ApplyShift(ValidateShiftBounds(pending, deltaDays), "direct");
                RequireCurrentContainment(pending, horizon);
                CheckOverlaps(result);
                var direct = DirectActivityIds(result);
                IncomingSafe(result, new HashSet<string>(direct, StringComparer.Ordinal));
                return CascadeForward(result, direct);
            }

            var totals = ExistingDirectShifts(normalized);
            int position = totals.FindIndex(pair => pair.Key == id);
            long current = position >= 0 ? totals[position].Value : 0;
            if (position >= 0)
            {
                totals[position] = new KeyValuePair<string, long>(id, current + deltaDays);
            }
            else
            {
                totals.Add(new KeyValuePair<string, long>(id, current + deltaDays));
            }

            totals.RemoveAll(pair => pair.Key == id && pair.Value == 0);
            var rebuilt = ResetPendingToOriginal(normalized);
            foreach (var pair in SortStable(totals, (left, right) => JsCompare.LocaleCompare(left.Key, right.Key)))
            {
                var pending = PendingFragments(rebuilt, pair.Key);
                RequireDirectContainment(pending, horizon, pair.Value);
                ApplyShift(ValidateShiftBounds(pending, pair.Value), "direct");
            }

            CheckOverlaps(rebuilt);
            var ids = totals.Select(pair => pair.Key).ToList();
            IncomingSafe(rebuilt, new HashSet<string>(ids, StringComparer.Ordinal));
            return CascadeForward(rebuilt, ids);
        }

        /// <summary>Recomputes the preview a model claims to be. JS: <c>recomputedPreview</c>, L969-985.</summary>
        public static WModel RecomputedPreview(WModel model, CivilHorizon horizon, bool allowHiddenTail = false)
        {
            var normalized = Preflight(model);
            if (!normalized.Fragments.Any(f => f.Provenance == "direct" && (f.Start != f.OriginalStart || f.Finish != f.OriginalFinish)))
            {
                return ResetPendingToOriginal(normalized);
            }

            if (HasNonUniformDirectState(normalized))
            {
                return RebuildDirectPreview(normalized);
            }

            var totals = ExistingDirectShifts(normalized);
            var rebuilt = ResetPendingToOriginal(normalized);
            foreach (var pair in SortStable(totals, (left, right) => JsCompare.LocaleCompare(left.Key, right.Key)))
            {
                var pending = PendingFragments(rebuilt, pair.Key);
                if (!allowHiddenTail)
                {
                    RequireDirectContainment(pending, horizon, pair.Value);
                }

                ApplyShift(ValidateShiftBounds(pending, pair.Value), "direct");
            }

            CheckOverlaps(rebuilt);
            var ids = totals.Select(pair => pair.Key).ToList();
            IncomingSafe(rebuilt, new HashSet<string>(ids, StringComparer.Ordinal));
            return CascadeForward(rebuilt, ids);
        }

        /// <summary>JS: <c>requirePreviewMatch</c>, L986-1002.</summary>
        public static void RequirePreviewMatch(WModel model, WModel preview)
        {
            var actual = new Dictionary<string, WFragment>(StringComparer.Ordinal);
            foreach (var fragment in model.Fragments)
            {
                if (actual.ContainsKey(S(fragment.Id)))
                {
                    throw Fail($"cobertura original ambigua: {S(fragment.Id)}");
                }

                actual[S(fragment.Id)] = fragment;
            }

            var expected = new Dictionary<string, WFragment>(StringComparer.Ordinal);
            var expectedOrder = new List<string>();
            foreach (var fragment in preview.Fragments)
            {
                if (expected.ContainsKey(S(fragment.Id)))
                {
                    throw Fail($"vista previa ambigua: {S(fragment.Id)}");
                }

                expected[S(fragment.Id)] = fragment;
                expectedOrder.Add(S(fragment.Id));
            }

            if (actual.Count != expected.Count || expectedOrder.Any(id => !actual.ContainsKey(id)))
            {
                throw Fail("cobertura original incompleta");
            }

            foreach (string id in expectedOrder)
            {
                var item = expected[id];
                var supplied = actual[id];
                if (supplied.Start != item.Start || supplied.Finish != item.Finish || (supplied.Provenance ?? string.Empty) != (item.Provenance ?? string.Empty))
                {
                    throw Fail($"vista previa no coincide: {id}");
                }
            }
        }

        private static void RequireSource(ProjectSource source)
        {
            if (source == null || string.IsNullOrEmpty(source.ProjectFingerprint) || string.IsNullOrEmpty(source.PrecedenceFingerprint))
            {
                throw Fail("identidad o fingerprint del proyecto ausente");
            }
        }

        private static int PackageOrder(WFragment left, WFragment right) => left.OriginalStart == right.OriginalStart
            ? JsCompare.LocaleCompare(S(left.Id), S(right.Id))
            : JsCompare.LocaleCompare(left.OriginalStart, right.OriginalStart);

        private static bool Moved(WFragment f) => f.Start != f.OriginalStart || f.Finish != f.OriginalFinish;

        private static bool DirectOrCascade(string provenance) => provenance == "direct" || provenance == "cascade";

        /// <summary>Builds a <c>vgs-stp-splits/2</c> scenario. JS: <c>buildScenario</c>, L1004-1033.</summary>
        public static ScenarioPackage BuildScenario(WModel model, ProjectSource source, CivilHorizon horizonValue)
        {
            RequireSource(source);
            var horizon = ValidateCivilHorizon(model, horizonValue);
            if (!model.Fragments.Any(Moved))
            {
                throw Fail("el escenario no contiene cambios directos o por cascada");
            }

            var preview = RecomputedPreview(model, horizon);
            RequirePreviewMatch(model, preview);
            var changed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var fragment in preview.Fragments)
            {
                if (!Moved(fragment) || !DirectOrCascade(fragment.Provenance))
                {
                    continue;
                }

                string id = ActivityId(fragment);
                if (changed.TryGetValue(id, out var previous) && previous != fragment.Provenance)
                {
                    throw Fail($"procedencia ambigua para actividad {id}");
                }

                changed[id] = fragment.Provenance;
            }

            var activities = SortStable(changed.Keys, JsCompare.Ordinal).Select(activityIdValue => new ScenarioActivity
            {
                ActivityId = activityIdValue,
                Provenance = changed[activityIdValue],
                Fragments = SortStable(preview.Fragments.Where(f => ActivityId(f) == activityIdValue), PackageOrder).Select(f => new ScenarioFragment
                {
                    Id = S(f.Id),
                    NativeBarXmlKey = f.NativeBarXmlKey ?? string.Empty,
                    NativeBarKey = f.NativeBarKey ?? string.Empty,
                    Start = f.Start,
                    Finish = f.Finish,
                    OriginalStart = f.OriginalStart,
                    OriginalFinish = f.OriginalFinish,
                    QuantityPercentage = f.QuantityPercentage ?? string.Empty,
                    LockedCompleted = Locked(f),
                }).ToArray(),
            }).ToArray();
            if (activities.Length == 0)
            {
                throw Fail("el escenario no contiene cambios directos o por cascada");
            }

            if (activities.Any(a => a.Fragments.Any(f => f.NativeBarXmlKey.Length == 0 || f.NativeBarKey.Length == 0 || f.QuantityPercentage.Length == 0)))
            {
                throw Fail("metadatos nativos o cantidades incompletos");
            }

            return new ScenarioPackage
            {
                Schema = "vgs-stp-splits/2",
                ProjectFingerprint = source.ProjectFingerprint,
                PrecedenceFingerprint = source.PrecedenceFingerprint,
                HorizonStart = horizon.HorizonStart,
                HorizonFinish = horizon.HorizonFinish,
                Activities = activities,
            };
        }

        /// <summary>Every fragment of every affected activity. JS: <c>serializeAllAffectedActivitiesByFragment</c>, L1035-1059.</summary>
        public static List<ApplyActivity> SerializeAllAffectedActivitiesByFragment(List<WFragment> fragments)
        {
            var affected = new HashSet<string>(fragments.Where(f => Moved(f) && DirectOrCascade(f.Provenance)).Select(ActivityId), StringComparer.Ordinal);
            return SortStable(affected, JsCompare.Ordinal).Select(id =>
            {
                var activityFragments = SortStable(fragments.Where(f => ActivityId(f) == id), PackageOrder);
                var unitDates = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var unit in LogicalUnits(fragments, id))
                {
                    foreach (var fragment in unit.Fragments)
                    {
                        unitDates[S(fragment.Id)] = unit.OriginalDate;
                    }
                }

                return new ApplyActivity
                {
                    ActivityId = id,
                    Fragments = activityFragments.Select(f =>
                    {
                        string unitDate = unitDates.TryGetValue(S(f.Id), out var date) ? date : string.Empty;
                        return new ApplyFragment
                        {
                            Id = S(f.Id),
                            NativeBarXmlKey = f.NativeBarXmlKey ?? string.Empty,
                            NativeBarKey = f.NativeBarKey ?? string.Empty,
                            LogicalUnitId = $"{id}:{unitDate}",
                            LogicalUnitDate = unitDate,
                            Provenance = !Moved(f) ? "unchanged" : f.Provenance ?? string.Empty,
                            Start = f.Start,
                            Finish = f.Finish,
                            OriginalStart = f.OriginalStart,
                            OriginalFinish = f.OriginalFinish,
                            QuantityPercentage = f.QuantityPercentage ?? string.Empty,
                            LockedCompleted = Locked(f),
                        };
                    }).ToArray(),
                };
            }).ToList();
        }

        /// <summary>Builds the <c>vgs-stp-apply/1</c> package. JS: <c>buildVgsApplyPackage</c>, L1061-1076.</summary>
        public static ApplyPackage BuildVgsApplyPackage(WModel model, ProjectSource source, CivilHorizon horizonValue)
        {
            RequireSource(source);
            var horizon = ValidateCivilHorizon(model, horizonValue);
            var preview = RecomputedPreview(model, horizon, true);
            RequirePreviewMatch(model, preview);
            var activities = SerializeAllAffectedActivitiesByFragment(preview.Fragments);
            if (activities.Count == 0)
            {
                throw Fail("el paquete de aplicación no contiene cambios directos o por cascada");
            }

            if (activities.Any(a => a.Fragments.Any(f =>
            {
                bool moved = f.Start != f.OriginalStart || f.Finish != f.OriginalFinish;
                bool validProvenance = moved ? DirectOrCascade(f.Provenance) : f.Provenance == "unchanged";
                return f.NativeBarXmlKey.Length == 0 || f.NativeBarKey.Length == 0 || f.QuantityPercentage.Length == 0 || !validProvenance;
            })))
            {
                throw Fail("metadatos nativos, cantidades o procedencia incompletos");
            }

            return new ApplyPackage
            {
                Schema = "vgs-stp-apply/1",
                ProjectFingerprint = source.ProjectFingerprint,
                PrecedenceFingerprint = source.PrecedenceFingerprint,
                HorizonStart = horizon.HorizonStart,
                HorizonFinish = horizon.HorizonFinish,
                Activities = activities.ToArray(),
            };
        }
    }
}
