using System;
using System.Linq;
using System.Text.RegularExpressions;
using Maptek.Vulcan.Stp.Engine.Internal;

namespace Maptek.Vulcan.Stp.Engine
{
    /// <summary>Outcome of a controller operation: <c>{ ok, message, code }</c>. JS: <c>result</c>, L237.</summary>
    public sealed class OperationResult
    {
        /// <summary>Outcome codes. JS: <c>outcomeCode</c>, L208-216, plus <c>local-changes</c> (L250).</summary>
        public static class Codes
        {
            /// <summary>Success.</summary>
            public const string None = "";

            /// <summary>Any other rejection.</summary>
            public const string InvalidMove = "invalid-move";

            /// <summary>Completed work is in the way.</summary>
            public const string CompletedWork = "completed-work";

            /// <summary>A precedence would get worse.</summary>
            public const string IncomingPrecedence = "incoming-precedence";

            /// <summary>A native anchor/limit is in the way.</summary>
            public const string NativeAnchor = "native-anchor";

            /// <summary>The move leaves the horizon.</summary>
            public const string HorizonRange = "horizon-range";

            /// <summary>A date leaves years 0001-9999.</summary>
            public const string CivilRange = "civil-range";

            /// <summary>The horizon is frozen by local moves.</summary>
            public const string LocalChanges = "local-changes";
        }

        /// <summary>Gets a value indicating whether the operation succeeded.</summary>
        public bool Ok { get; init; }

        /// <summary>Gets the Spanish message.</summary>
        public string Message { get; init; }

        /// <summary>Gets the outcome code (see <see cref="Codes"/>).</summary>
        public string Code { get; init; }
    }

    /// <summary>JS: return value of <c>activityEligibility</c>, L363-372.</summary>
    public sealed class ActivityEligibility
    {
        /// <summary>Gets a value indicating whether move controls are shown.</summary>
        public bool ShowControls { get; init; }

        /// <summary>Gets a value indicating whether a −1 day move is possible.</summary>
        public bool BackwardEnabled { get; init; }

        /// <summary>Gets a value indicating whether a +1 day move is possible.</summary>
        public bool ForwardEnabled { get; init; }

        /// <summary>Gets the guidance text, or empty.</summary>
        public string Guidance { get; init; }
    }

    /// <summary>JS: return value of <c>fragmentEligibility</c>, L373-391.</summary>
    public sealed class FragmentEligibility
    {
        /// <summary>Gets a value indicating whether the fragment's unit can be moved.</summary>
        public bool Eligible { get; init; }

        /// <summary>Gets the reason: empty, <c>completed</c> or <c>out-of-range</c>.</summary>
        public string Reason { get; init; }

        /// <summary>Gets the guidance text, or empty.</summary>
        public string Guidance { get; init; }
    }

    /// <summary>The visible (inclusive) horizon range. JS: <c>horizonDisplay</c>, L243-247.</summary>
    public sealed class HorizonDisplay
    {
        /// <summary>Gets the first visible day <c>YYYY-MM-DD</c>.</summary>
        public string Start { get; init; }

        /// <summary>Gets the last visible day <c>YYYY-MM-DD</c>.</summary>
        public string Finish { get; init; }

        /// <summary>Gets the duration in days.</summary>
        public int? DurationDays { get; init; }
    }

    /// <summary>Controller state. JS: return value of <c>state()</c>, L320-333.</summary>
    public sealed class ControllerState
    {
        /// <summary>Gets the current model.</summary>
        public PlannerModel Model { get; init; }

        /// <summary>Gets the horizon, or <c>null</c> when there is no pending work.</summary>
        public CivilHorizon Horizon { get; init; }

        /// <summary>Gets a value indicating whether the horizon is frozen (local moves exist).</summary>
        public bool HorizonLocked { get; init; }

        /// <summary>Gets a value indicating whether local moves exist.</summary>
        public bool HasLocalMovements { get; init; }

        /// <summary>Gets the current warning.</summary>
        public string Warning { get; init; }

        /// <summary>Gets the visible range, or <c>null</c>.</summary>
        public HorizonDisplay DisplayHorizon { get; init; }

        /// <summary>Gets a value indicating whether any fragment is pending.</summary>
        public bool HasPending { get; init; }

        /// <summary>Gets a value indicating whether the horizon controls are disabled.</summary>
        public bool HorizonControlsDisabled { get; init; }

        /// <summary>Gets a value indicating whether a package can be prepared.</summary>
        public bool CanDownload { get; init; }

        /// <summary>Gets the status line.</summary>
        public string Status { get; init; }
    }

    /// <summary>
    /// Stateful planner session over one snapshot: horizon (from seed, stored preference or derived), moves, tail
    /// moves, stagger, horizon edits and preference save. JS: <c>createCivilHorizonController</c>, L218-397.
    /// </summary>
    public sealed class CivilHorizonController
    {
        private const string NoPending = "No hay trabajo pendiente para planificar.";
        private const string HorizonDirectionGuidance = "Extienda el rango para habilitar esta dirección.";
        private const string CivilRangeDirectionGuidance = "Movimiento no disponible: esta dirección excede el rango civil compatible (años 0001 a 9999).";
        private const string UnavailableDirectionGuidance = "Movimiento no disponible en esta dirección.";

        private static readonly Regex CompletedPattern = new Regex("completad[oa]|avance completado", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex PrecedencePattern = new Regex("precedencia|predecesor", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AnchorPattern = new Regex("ancla|límite nativo", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly Func<PlannerModel> modelFactory;
        private readonly string projectKey;
        private readonly IHorizonStorage storage;
        private Session current;

        /// <summary>Initializes a new instance of the <see cref="CivilHorizonController"/> class.</summary>
        /// <param name="modelFactory">Produces the snapshot model (called again on <see cref="Reset"/>).</param>
        /// <param name="projectKey">The project key (horizon preference key).</param>
        /// <param name="storage">Preference storage.</param>
        /// <param name="initialHorizon">Optional seed horizon (overrides the stored preference).</param>
        public CivilHorizonController(Func<PlannerModel> modelFactory, string projectKey, IHorizonStorage storage, CivilHorizon initialHorizon = null)
        {
            this.modelFactory = modelFactory ?? throw EngineException.Fail("fábrica de modelo inválida");
            this.projectKey = projectKey;
            this.storage = storage;
            current = MakeState(initialHorizon);
        }

        /// <summary>JS: <c>outcomeCode</c>, L208-216.</summary>
        /// <param name="error">The error.</param>
        /// <returns>The outcome code.</returns>
        public static string OutcomeCode(EngineException error)
        {
            if (error?.Code == EngineException.CivilRange)
            {
                return OperationResult.Codes.CivilRange;
            }

            if (error?.Code == EngineException.HorizonRange)
            {
                return OperationResult.Codes.HorizonRange;
            }

            string message = error?.Message ?? string.Empty;
            if (CompletedPattern.IsMatch(message))
            {
                return OperationResult.Codes.CompletedWork;
            }

            if (PrecedencePattern.IsMatch(message))
            {
                return OperationResult.Codes.IncomingPrecedence;
            }

            return AnchorPattern.IsMatch(message) ? OperationResult.Codes.NativeAnchor : OperationResult.Codes.InvalidMove;
        }

        /// <summary>Current state. JS: <c>state()</c>, L320-333.</summary>
        /// <returns>The state.</returns>
        public ControllerState State()
        {
            bool noPending = !HasPending();
            bool dirty = ModelHasLocalMovements(current.Model);
            return new ControllerState
            {
                Model = current.Model.ToPublic(),
                Horizon = current.Horizon,
                HorizonLocked = dirty,
                HasLocalMovements = dirty,
                Warning = current.Warning,
                DisplayHorizon = current.Horizon == null ? null : new HorizonDisplay
                {
                    Start = current.Horizon.HorizonStart.Substring(0, 10),
                    Finish = CivilTime.AddDays(current.Horizon.HorizonFinish, -1).Substring(0, 10),
                    DurationDays = current.Horizon.DurationDays,
                },
                HasPending = !noPending,
                HorizonControlsDisabled = current.Horizon == null,
                CanDownload = current.Horizon != null && dirty,
                Status = noPending ? NoPending : current.Warning,
            };
        }

        /// <summary>Moves the horizon start, keeping the duration. JS: <c>editStart</c>, L334-339.</summary>
        /// <param name="value">New start day <c>YYYY-MM-DD</c>.</param>
        /// <returns>The outcome.</returns>
        public OperationResult EditStart(string value) => Commit(() =>
        {
            string start = value + "T00:00:00";
            return new CivilHorizon { HorizonStart = start, HorizonFinish = CivilTime.AddDays(start, current.Horizon.DurationDays ?? 0) };
        });

        /// <summary>Sets the last visible day. JS: <c>editFinish</c>, L340-342.</summary>
        /// <param name="value">New last day <c>YYYY-MM-DD</c>.</param>
        /// <returns>The outcome.</returns>
        public OperationResult EditFinish(string value) => Commit(() =>
            new CivilHorizon { HorizonStart = current.Horizon.HorizonStart, HorizonFinish = CivilTime.AddDays(value + "T00:00:00", 1) });

        /// <summary>Sets the duration in days. JS: <c>editDuration</c>, L343-345.</summary>
        /// <param name="value">New duration.</param>
        /// <returns>The outcome.</returns>
        public OperationResult EditDuration(long value) => Commit(() =>
            new CivilHorizon { HorizonStart = current.Horizon.HorizonStart, HorizonFinish = CivilTime.AddDays(current.Horizon.HorizonStart, value) });

        /// <summary>Persists the current horizon. JS: <c>savePreference</c>, L346-352.</summary>
        /// <returns>The outcome.</returns>
        public OperationResult SavePreference()
        {
            if (current.Horizon == null)
            {
                return Result(false, NoPending, OperationResult.Codes.InvalidMove);
            }

            var saved = PlannerLogic.SaveHorizonPreference(storage, projectKey, current.Horizon);
            return Result(saved.Persisted, string.IsNullOrEmpty(saved.Warning) ? "Configuración de horizonte guardada." : saved.Warning, saved.Persisted ? string.Empty : OperationResult.Codes.InvalidMove);
        }

        /// <summary>Alias of <see cref="SavePreference"/>. JS: <c>save</c>, L353.</summary>
        /// <returns>The outcome.</returns>
        public OperationResult Save() => SavePreference();

        /// <summary>Whole-activity day move. JS: <c>performMove</c> (<c>moveActivity</c>/<c>move</c>), L267-275.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="days">Non-zero day shift.</param>
        /// <returns>The outcome.</returns>
        public OperationResult MoveActivity(string activityId, long days)
        {
            if (current.Horizon == null)
            {
                return Result(false, NoPending, OperationResult.Codes.InvalidMove);
            }

            return Guard(() =>
            {
                Accept(Rules.MoveActivity(current.Model, activityId, days, current.Horizon));
                return Result(true, "Movimiento validado.");
            });
        }

        /// <summary>Alias of <see cref="MoveActivity"/>. JS: <c>move</c>, L355.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="days">Non-zero day shift.</param>
        /// <returns>The outcome.</returns>
        public OperationResult Move(string activityId, long days) => MoveActivity(activityId, days);

        /// <summary>Moves a day and the rest of the tail to a visible target day. JS: <c>performDailyTailMove</c>, L276-289.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="fragmentId">A fragment of the day to move.</param>
        /// <param name="targetDate">Target day <c>YYYY-MM-DD</c>.</param>
        /// <returns>The outcome.</returns>
        public OperationResult MoveDailyTail(string activityId, string fragmentId, string targetDate)
        {
            if (current.Horizon == null)
            {
                return Result(false, NoPending, OperationResult.Codes.InvalidMove);
            }

            return Guard(() =>
            {
                long target = CivilTime.Scalar(Js.S(targetDate) + "T00:00:00");
                long lower = CivilTime.Scalar(current.Horizon.HorizonStart), upper = CivilTime.Scalar(current.Horizon.HorizonFinish);
                if (target < lower || target >= upper)
                {
                    throw EngineException.Fail("fecha destino fuera del horizonte visible");
                }

                Accept(Rules.MoveDailyTail(current.Model, new DailyTailMoveRequest { ActivityId = activityId, FragmentId = fragmentId, TargetDate = targetDate }));
                return Result(true, "Movimiento validado.");
            });
        }

        /// <summary>Moves a day and the rest of the tail to the first day after the horizon. JS: <c>deferDailyTailToNextPeriod</c>, L290-304.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="fragmentId">A fragment of the day to move.</param>
        /// <returns>The outcome.</returns>
        public OperationResult DeferDailyTailToNextPeriod(string activityId, string fragmentId)
        {
            if (current.Horizon == null)
            {
                return Result(false, NoPending, OperationResult.Codes.InvalidMove);
            }

            return Guard(() =>
            {
                string targetDate = current.Horizon.HorizonFinish.Substring(0, 10);
                Accept(Rules.MoveDailyTail(current.Model, new DailyTailMoveRequest { ActivityId = activityId, FragmentId = fragmentId, TargetDate = targetDate }));
                return Result(true, $"La cola diaria se movió al siguiente periodo, a partir del {targetDate.Substring(8, 2)}/{targetDate.Substring(5, 2)}.");
            });
        }

        /// <summary>Split-and-stagger. JS: <c>performDailyTailStagger</c>, L305-318.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="fragmentId">A fragment of the day after which gaps are inserted.</param>
        /// <param name="divisions">Number of gaps.</param>
        /// <param name="gapDays">Days per gap.</param>
        /// <returns>The outcome.</returns>
        public OperationResult StaggerDailyTail(string activityId, string fragmentId, long divisions, long gapDays)
        {
            if (current.Horizon == null)
            {
                return Result(false, NoPending, OperationResult.Codes.InvalidMove);
            }

            return Guard(() =>
            {
                Accept(Rules.StaggerDailyTail(current.Model, new DailyStaggerRequest { ActivityId = activityId, FragmentId = fragmentId, Divisions = divisions, GapDays = gapDays }, current.Horizon));
                return Result(true, $"Se insertaron {divisions} desfases de {gapDays} {(gapDays == 1 ? "día" : "días")}.");
            });
        }

        /// <summary>Back to the snapshot (re-reads the preference). JS: <c>reset</c>, L359-362.</summary>
        /// <returns>The outcome.</returns>
        public OperationResult Reset()
        {
            current = MakeState(null);
            return Result(true, current.Horizon != null
                ? (string.IsNullOrEmpty(current.Warning) ? "Se restableció la instantánea, sin cambios pendientes." : current.Warning)
                : NoPending);
        }

        /// <summary>Whether ±1 day moves of an activity are possible. JS: <c>activityEligibility</c>, L363-372.</summary>
        /// <param name="activityId">The activity.</param>
        /// <returns>The eligibility.</returns>
        public ActivityEligibility ActivityEligibility(string activityId)
        {
            string id = Js.S(activityId);
            var pending = current.Model.Fragments.Where(f => Rules.ActivityId(f) == id && !Rules.Locked(f)).ToList();
            bool withinOriginalRange = current.Horizon != null && pending.All(f =>
                string.CompareOrdinal(f.OriginalStart, current.Horizon.HorizonStart) >= 0
                && string.CompareOrdinal(f.OriginalFinish, current.Horizon.HorizonFinish) <= 0);
            bool showControls = pending.Count > 0 && withinOriginalRange;
            if (!showControls)
            {
                return new ActivityEligibility { ShowControls = false, BackwardEnabled = false, ForwardEnabled = false, Guidance = "Extienda el rango para incluir toda la actividad pendiente." };
            }

            var backward = DirectionFits(activityId, -1);
            var forward = DirectionFits(activityId, 1);
            var blocked = new[] { backward, forward }.Where(item => !item.Enabled).ToList();
            string guidance = blocked.FirstOrDefault(item => item.Reason == "civil-range")?.Guidance ?? blocked.FirstOrDefault()?.Guidance ?? string.Empty;
            return new ActivityEligibility { ShowControls = true, BackwardEnabled = backward.Enabled, ForwardEnabled = forward.Enabled, Guidance = guidance };
        }

        /// <summary>Whether a fragment's day can be tail-moved. JS: <c>fragmentEligibility</c>, L373-391.</summary>
        /// <param name="fragmentId">The fragment id.</param>
        /// <returns>The eligibility.</returns>
        /// <exception cref="EngineException">Unknown fragment or unit (not caught, like the JS).</exception>
        public FragmentEligibility FragmentEligibility(string fragmentId)
        {
            var fragment = current.Model.Fragments.FirstOrDefault(item => item.Id == fragmentId);
            if (fragment == null)
            {
                throw EngineException.Fail($"fragmento desconocido: {fragmentId ?? string.Empty}");
            }

            var unit = Rules.LogicalUnits(current.Model, Rules.ActivityId(fragment)).FirstOrDefault(item => item.Fragments.Any(part => part.Id == fragment.Id));
            if (unit == null)
            {
                throw EngineException.Fail($"unidad diaria desconocida: {fragment.Id}");
            }

            if (unit.Fragments.Any(Rules.Locked))
            {
                return new FragmentEligibility { Eligible = false, Reason = "completed", Guidance = "El trabajo completado no se puede mover." };
            }

            bool visible = false;
            if (current.Horizon != null)
            {
                long lower = CivilTime.Scalar(current.Horizon.HorizonStart), upper = CivilTime.Scalar(current.Horizon.HorizonFinish);
                visible = unit.Fragments.Any(part => CivilTime.Scalar(part.Finish) > lower && CivilTime.Scalar(part.Start) < upper);
            }

            if (!visible)
            {
                return new FragmentEligibility { Eligible = false, Reason = "out-of-range", Guidance = "La unidad está fuera del horizonte visible." };
            }

            return new FragmentEligibility { Eligible = true, Reason = string.Empty, Guidance = string.Empty };
        }

        /// <summary>Display state of a fragment: <c>completed</c>, <c>in-range</c> or <c>out-of-range</c>. JS: <c>fragmentState</c>, L392-395.</summary>
        /// <param name="fragment">The fragment.</param>
        /// <returns>The state name.</returns>
        public string FragmentState(PlannerFragment fragment)
        {
            if (fragment.LockedCompleted)
            {
                return "completed";
            }

            return current.Horizon != null && string.CompareOrdinal(fragment.Start, current.Horizon.HorizonStart) >= 0
                && string.CompareOrdinal(fragment.Finish, current.Horizon.HorizonFinish) <= 0 ? "in-range" : "out-of-range";
        }

        private static OperationResult Result(bool ok, string message, string code = "") => new OperationResult { Ok = ok, Message = message, Code = code };

        private static bool ModelHasLocalMovements(WModel model) => model.Fragments.Any(f =>
            (f.Provenance == "direct" || f.Provenance == "cascade") && (f.Start != f.OriginalStart || f.Finish != f.OriginalFinish));

        private static OperationResult Guard(Func<OperationResult> action)
        {
            try
            {
                return action();
            }
            catch (EngineException error)
            {
                return Result(false, error.Message, OutcomeCode(error));
            }
        }

        // JS: makeState, L220-235.
        private Session MakeState(CivilHorizon seedHorizon)
        {
            var model = Rules.Preflight(WModel.From(modelFactory()), true).Clone();
            var preference = HorizonPreferences.Load(storage, projectKey);
            bool pending = model.Fragments.Any(f => !Rules.Locked(f));
            CivilHorizon horizon = null;
            if (pending && seedHorizon != null)
            {
                horizon = Rules.ValidateCivilHorizon(model, seedHorizon);
            }
            else if (pending && preference.StartDay != null)
            {
                try
                {
                    string start = preference.StartDay + "T00:00:00";
                    horizon = Rules.ValidateCivilHorizon(model, new CivilHorizon { HorizonStart = start, HorizonFinish = CivilTime.AddDays(start, preference.DurationDays) });
                }
                catch (EngineException)
                {
                    horizon = null;
                }
            }

            if (pending && horizon == null)
            {
                horizon = Rules.DeriveCivilHorizon(model, preference.DurationDays);
            }

            return new Session { Model = model, Horizon = horizon, Warning = seedHorizon != null ? string.Empty : preference.Warning };
        }

        private bool HasPending() => current.Model.Fragments.Any(f => !Rules.Locked(f));

        private void Accept(WModel model)
        {
            bool dirty = ModelHasLocalMovements(model);
            current = new Session { Model = model, Horizon = current.Horizon, HorizonLocked = dirty, HasLocalMovements = dirty, Warning = string.Empty };
        }

        // JS: commit, L248-256.
        private OperationResult Commit(Func<CivilHorizon> candidateFactory)
        {
            if (current.Horizon == null)
            {
                return Result(false, NoPending, OperationResult.Codes.InvalidMove);
            }

            if (ModelHasLocalMovements(current.Model))
            {
                return Result(false, "El rango no se puede cambiar mientras hay movimientos locales.", OperationResult.Codes.LocalChanges);
            }

            return Guard(() =>
            {
                var horizon = Rules.ValidateCivilHorizon(current.Model, candidateFactory());
                current = new Session { Model = current.Model, Horizon = horizon, HorizonLocked = current.HorizonLocked, HasLocalMovements = current.HasLocalMovements, Warning = string.Empty };
                return Result(true, "Horizonte civil actualizado.");
            });
        }

        // JS: directionFits, L257-266.
        private Direction DirectionFits(string activityId, long days)
        {
            try
            {
                Rules.MoveActivity(current.Model, activityId, days, current.Horizon);
                return new Direction { Enabled = true, Reason = string.Empty, Guidance = string.Empty };
            }
            catch (EngineException error) when (error.Code == EngineException.CivilRange)
            {
                return new Direction { Enabled = false, Reason = "civil-range", Guidance = CivilRangeDirectionGuidance };
            }
            catch (EngineException error) when (error.Code == EngineException.HorizonRange)
            {
                return new Direction { Enabled = false, Reason = "horizon", Guidance = HorizonDirectionGuidance };
            }
            catch (EngineException)
            {
                return new Direction { Enabled = false, Reason = "unavailable", Guidance = UnavailableDirectionGuidance };
            }
        }

        private sealed class Direction
        {
            public bool Enabled;
            public string Reason;
            public string Guidance;
        }

        private sealed class Session
        {
            public WModel Model;
            public CivilHorizon Horizon;
            public bool HorizonLocked;
            public bool HasLocalMovements;
            public string Warning;
        }
    }
}
