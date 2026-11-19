using System.Collections.Generic;
using System.Linq;

namespace Maptek.Vulcan.Stp.Engine.Internal
{
    // Mutable mirror of the JS objects the rules shuffle around. The public API is immutable
    // (PlannerModel & co.); every public entry point converts in and out, and every rule clones
    // before it mutates, exactly like the JS `clone(...)` calls (planner_logic.js L398).

    /// <summary>Mutable fragment (JS fragment object).</summary>
    internal sealed class WFragment
    {
        public string Id;
        public string ActivityId;
        public string NativeBarKey;
        public string NativeBarXmlKey;
        public string Start;
        public string Finish;
        public string OriginalStart;
        public string OriginalFinish;
        public string LogicalUnitDate;
        public bool? DisplaySuppressed;
        public string QuantityPercentage;
        public bool LockedCompleted;
        public string Provenance;
        public string AnchorBefore;
        public string AnchorAfter;
        public int? SplitOrder;

        public WFragment Clone() => (WFragment)MemberwiseClone();

        public static WFragment From(PlannerFragment f) => new WFragment
        {
            Id = f.Id,
            ActivityId = f.ActivityId,
            NativeBarKey = f.NativeBarKey,
            NativeBarXmlKey = f.NativeBarXmlKey,
            Start = f.Start,
            Finish = f.Finish,
            OriginalStart = f.OriginalStart,
            OriginalFinish = f.OriginalFinish,
            LogicalUnitDate = f.LogicalUnitDate,
            DisplaySuppressed = f.DisplaySuppressed,
            QuantityPercentage = f.QuantityPercentage,
            LockedCompleted = f.LockedCompleted,
            Provenance = f.Provenance,
            AnchorBefore = f.AnchorBefore,
            AnchorAfter = f.AnchorAfter,
            SplitOrder = f.SplitOrder,
        };

        public PlannerFragment ToPublic() => new PlannerFragment
        {
            Id = Id,
            ActivityId = ActivityId,
            NativeBarKey = NativeBarKey,
            NativeBarXmlKey = NativeBarXmlKey,
            Start = Start,
            Finish = Finish,
            OriginalStart = OriginalStart,
            OriginalFinish = OriginalFinish,
            LogicalUnitDate = LogicalUnitDate,
            DisplaySuppressed = DisplaySuppressed,
            QuantityPercentage = QuantityPercentage,
            LockedCompleted = LockedCompleted,
            Provenance = Provenance,
            AnchorBefore = AnchorBefore,
            AnchorAfter = AnchorAfter,
            SplitOrder = SplitOrder,
        };
    }

    /// <summary>Mutable precedence link (JS link object).</summary>
    internal sealed class WLink
    {
        public string Key;
        public string PredecessorId;
        public string SuccessorId;
        public string PredecessorBar;
        public string SuccessorBar;
        public string Type;
        public string LagMicroseconds;
        public long? LagSeconds;
        public string GroupKey;

        public WLink Clone() => (WLink)MemberwiseClone();

        public static WLink From(PrecedenceLink l) => new WLink
        {
            Key = l.Key,
            PredecessorId = l.PredecessorId,
            SuccessorId = l.SuccessorId,
            PredecessorBar = l.PredecessorBar,
            SuccessorBar = l.SuccessorBar,
            Type = l.Type,
            LagMicroseconds = l.LagMicroseconds,
            LagSeconds = l.LagSeconds,
            GroupKey = l.GroupKey,
        };

        public PrecedenceLink ToPublic() => new PrecedenceLink
        {
            Key = Key,
            PredecessorId = PredecessorId,
            SuccessorId = SuccessorId,
            PredecessorBar = PredecessorBar,
            SuccessorBar = SuccessorBar,
            Type = Type,
            LagMicroseconds = LagMicroseconds,
            LagSeconds = LagSeconds,
            GroupKey = GroupKey,
        };
    }

    /// <summary>Activity (JS <c>{ id, nativeBars }</c>). Bars are immutable and shared.</summary>
    internal sealed class WActivity
    {
        public string Id;
        public List<NativeBar> NativeBars;

        public WActivity Clone() => new WActivity { Id = Id, NativeBars = NativeBars?.ToList() };
    }

    /// <summary>Mutable model (JS model object).</summary>
    internal sealed class WModel
    {
        public List<WActivity> Activities = new List<WActivity>();
        public List<WLink> Precedences = new List<WLink>();
        public List<WFragment> Fragments = new List<WFragment>();

        /// <summary>JS: <c>clone</c>, L398.</summary>
        public WModel Clone() => new WModel
        {
            Activities = Activities.Select(a => a.Clone()).ToList(),
            Precedences = Precedences.Select(l => l.Clone()).ToList(),
            Fragments = Fragments.Select(f => f.Clone()).ToList(),
        };

        public static WModel From(PlannerModel model)
        {
            if (model == null)
            {
                return new WModel();
            }

            return new WModel
            {
                Activities = (model.Activities ?? new PlannerActivity[0])
                    .Select(a => new WActivity { Id = a?.Id, NativeBars = a?.NativeBars?.ToList() }).ToList(),
                Precedences = (model.Precedences ?? new PrecedenceLink[0]).Select(WLink.From).ToList(),
                Fragments = (model.Fragments ?? new PlannerFragment[0]).Select(WFragment.From).ToList(),
            };
        }

        public PlannerModel ToPublic() => new PlannerModel
        {
            Activities = Activities.Select(a => new PlannerActivity { Id = a.Id, NativeBars = a.NativeBars?.ToArray() }).ToArray(),
            Precedences = Precedences.Select(l => l.ToPublic()).ToArray(),
            Fragments = Fragments.Select(f => f.ToPublic()).ToArray(),
        };
    }

    /// <summary>A logical unit: the fragments of one activity sharing a civil day. JS: <c>logicalUnits</c> items, L585-592.</summary>
    internal sealed class LogicalUnit
    {
        public string Id;
        public string OriginalDate;
        public List<WFragment> Fragments;
        public string CurrentDate;
    }
}
