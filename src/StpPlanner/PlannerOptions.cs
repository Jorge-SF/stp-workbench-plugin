using System;
using System.Collections.Generic;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>Row grouping of the planner grid ("Agrupar").</summary>
    public enum PlannerGrouping
    {
        /// <summary>By activity type (default).</summary>
        Type,

        /// <summary>By level.</summary>
        Level,

        /// <summary>By resource.</summary>
        Resource,

        /// <summary>No groups.</summary>
        None,
    }

    /// <summary>Row order inside a group ("Ordenar").</summary>
    public enum PlannerSort
    {
        /// <summary>By the first visible start, then snapshot order (default).</summary>
        Start,

        /// <summary>By activity id.</summary>
        Id,

        /// <summary>By a grid attribute (<see cref="PlannerSession.SortAttribute"/>), numbers numerically, blanks last.</summary>
        Attribute,

        /// <summary>Snapshot (VGS) order.</summary>
        Source,
    }

    /// <summary>Grid column names and other settings that do not change while a session is open.</summary>
    public sealed class PlannerOptions
    {
        /// <summary>Gets the type columns. Default: <c>Activity Type</c>, as timeline_logic.js <c>activityType</c>.</summary>
        public IReadOnlyList<string> TypeColumns { get; init; } = new[] { "Activity Type" };

        /// <summary>Gets the level columns.</summary>
        public IReadOnlyList<string> LevelColumns { get; init; } = new[] { "Nivel", "Level" };

        /// <summary>Gets the resource columns, as timeline_logic.js <c>activityResource</c>.</summary>
        public IReadOnlyList<string> ResourceColumns { get; init; } = new[] { "Resource", "Resources", "Recurso", "Equipo" };

        /// <summary>Gets the daily rate columns, as timeline_logic.js <c>activityRate</c>.</summary>
        public IReadOnlyList<string> RateColumns { get; init; } = new[] { "Rate" };

        /// <summary>Gets the quantity unit columns.</summary>
        public IReadOnlyList<string> UnitColumns { get; init; } = new[] { "Unit", "Units", "Unidad" };

        /// <summary>Gets the daily objective per activity type (capacity panel "meta"); 0 or missing means none.</summary>
        public IReadOnlyDictionary<string, double> TypeObjectives { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Gets the maximum number of undo steps kept.</summary>
        public int UndoLimit { get; init; } = 100;
    }
}
