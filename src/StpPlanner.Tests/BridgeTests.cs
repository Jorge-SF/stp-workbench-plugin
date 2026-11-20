using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maptek.Vulcan.Stp.Bridge;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Planner.Tests
{
    /// <summary>Apply package submission and refresh against a fake bridge.</summary>
    public class BridgeTests
    {
        private static (PlannerSession Session, FakeBridge Bridge) Live(SnapshotV2 snapshot = null, string policy = null)
        {
            snapshot = snapshot ?? ProjectionTests.ChainWithRates();
            var bridge = new FakeBridge(snapshot);
            return (new PlannerSession(snapshot, Golden.LiveConfig(snapshot, policy), bridge, new MemoryHorizonStorage(), new PlannerOptions { TypeColumns = new[] { "Type" } }), bridge);
        }

        [Fact]
        public async Task ApplySubmitsTheCurrentPackage()
        {
            var (session, bridge) = Live();
            Assert.True(session.IsLive);
            Assert.Equal("En vivo", session.ConnectionText);
            Assert.False(session.CanApply);
            Assert.Equal(PlannerMessages.NoLocalMoves, session.ApplyBlockedReason);

            session.MoveActivity("A100", 1);
            Assert.True(session.CanApply, session.ApplyBlockedReason);
            var outcome = await session.ApplyToVgsAsync();
            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(PlannerMessages.PackageReady, session.StatusMessage);
            Assert.True(session.IsPackageReady);
            Assert.Equal(session.BuildApplyPackage().ComputeSha256(), bridge.Submitted.Single().ComputeSha256());
            Assert.Equal(new[] { "A100", "B200", "C300" }, bridge.Submitted.Single().Activities.Select(a => a.ActivityId));
            Assert.Equal(bridge.Submitted.Single().ComputeSha256(), session.LastReceipt.PackageSha256);

            session.MoveActivity("D400", 1);
            Assert.False(session.IsPackageReady);
            Assert.Equal(PlannerMessages.PlanChangedAfterReady, session.StatusMessage);
            Assert.True(session.StatusIsError);
        }

        [Fact]
        public async Task StalePackageMarksTheSnapshotOutOfDate()
        {
            var (session, bridge) = Live();
            session.MoveActivity("A100", 1);
            bridge.SubmitError = new StpBridgeException(StpBridgeErrorKind.Stale, "project stale", 409, "project stale");
            var outcome = await session.ApplyToVgsAsync();
            Assert.False(outcome.Ok);
            Assert.Equal(PlannerMessages.SnapshotStale, outcome.Message);
            Assert.True(session.IsSnapshotOutOfDate);
            Assert.False(session.CanApply);
            Assert.Equal(PlannerMessages.SnapshotStale, session.ApplyBlockedReason);

            var refreshed = await session.RefreshFromVgsAsync(_ => true);
            Assert.True(refreshed.Ok, refreshed.Message);
            Assert.False(session.IsSnapshotOutOfDate);
        }

        [Fact]
        public async Task UnsafePackageNamesTheActivities()
        {
            var (session, bridge) = Live();
            session.MoveActivity("A100", 1);
            var diagnostic = UnsafeApplyDiagnostic.FromJson(new JObject { ["reason"] = "precedencia", ["activities"] = new JArray("B200") });
            bridge.SubmitError = new StpBridgeException(StpBridgeErrorKind.ApplyUnsafe, "apply package unsafe", 422, "apply package unsafe", diagnostic);
            var outcome = await session.ApplyToVgsAsync();
            Assert.Equal("El paquete fue rechazado por seguridad (422): precedencia agravada. Actividad: B200 Fortificación rampa N1.", outcome.Message);
            Assert.False(session.IsPackageReady);
        }

        [Theory]
        [InlineData(StpBridgeErrorKind.InvalidRequest, "El paquete no tiene un formato válido (422). Revisa los movimientos y vuelve a generar el paquete.")]
        [InlineData(StpBridgeErrorKind.Unreachable, "No se pudo conectar con el puente local. Vuelva a ejecutar 2. VGS_ShortTermPlanner.py.")]
        [InlineData(StpBridgeErrorKind.Forbidden, "El puente local rechazó la autorización (403). Vuelva a abrir el planificador desde VGS.")]
        [InlineData(StpBridgeErrorKind.Busy, "El puente local está ocupado. Intente de nuevo en unos segundos.")]
        [InlineData(StpBridgeErrorKind.ProtocolViolation, "El puente no confirmó el paquete exacto.")]
        [InlineData(StpBridgeErrorKind.SaveIndeterminate, "No se pudo confirmar si el paquete se guardó. Revise C:\\respaldo\\pending.json antes de continuar.")]
        public async Task BridgeErrorsBecomeSpanishMessages(StpBridgeErrorKind kind, string message)
        {
            var (session, bridge) = Live();
            session.MoveActivity("A100", 1);
            bridge.SubmitError = new StpBridgeException(kind, "x", recoveryPath: kind == StpBridgeErrorKind.SaveIndeterminate ? "C:\\respaldo\\pending.json" : null);
            var outcome = await session.ApplyToVgsAsync();
            Assert.False(outcome.Ok);
            Assert.Equal(message, outcome.Message);
            Assert.Equal(kind.ToString(), outcome.Code);
            Assert.Equal(message, session.StatusMessage);
            Assert.False(session.IsBusy);
            Assert.True(session.HasPendingChanges); // the local plan is kept
        }

        [Fact]
        public async Task StaticSessionCannotApplyOrRefresh()
        {
            var session = ProjectionTests.Session();
            session.MoveActivity("A100", 1);
            Assert.False(session.IsLive);
            Assert.Equal("Sin conexión", session.ConnectionText);
            Assert.Equal(PlannerMessages.NotLive, session.ApplyBlockedReason);
            Assert.False((await session.ApplyToVgsAsync()).Ok);
            Assert.False(session.CanRefresh);
            Assert.False((await session.RefreshFromVgsAsync()).Ok);
            Assert.NotNull(session.BuildApplyPackage()); // the package can still be built (saved to a file by the view)
        }

        [Fact]
        public void InheritedConflictsBlockApplyUnderBlockPolicy()
        {
            var snapshot = Golden.Snapshot("precedence_conflict");
            var testCase = Golden.Load(Path.Combine(Golden.Expected, "precedence_conflict.push_beyond_inherited_deficit.json"));
            foreach (string policy in new[] { BridgeConfig.BlockInherited, BridgeConfig.AllowInherited })
            {
                var session = new PlannerSession(snapshot, Golden.LiveConfig(snapshot, policy), new FakeBridge(snapshot), new MemoryHorizonStorage());
                Assert.NotEmpty(session.ConflictAssessment.Inherited);
                Assert.Equal(PlannerMessages.ConflictNotice(session.ConflictAssessment), session.StatusMessage);
                foreach (var step in testCase["steps"].Select(s => s["step"])) GoldenSessionTests.Run(session, step);
                Assert.True(session.State.CanDownload);
                if (policy == BridgeConfig.BlockInherited)
                {
                    Assert.Equal(PlannerMessages.InheritedConflictsBlock, session.ApplyBlockedReason);
                    Assert.Equal("Política de precedencias: bloquear heredadas", session.PolicyText);
                }
                else
                {
                    Assert.True(session.CanApply, session.ApplyBlockedReason);
                    Assert.Equal("Política de precedencias: permitir heredadas", session.PolicyText);
                }
            }
        }

        [Fact]
        public async Task RefreshAsksBeforeDiscardingMoves()
        {
            var (session, bridge) = Live();
            session.MoveActivity("A100", 1);
            string asked = null;
            var cancelled = await session.RefreshFromVgsAsync(message => { asked = message; return false; });
            Assert.False(cancelled.Ok);
            Assert.Equal(PlannerMessages.RefreshDiscardsChanges, asked);
            Assert.True(session.HasPendingChanges);
            Assert.Equal(0, bridge.SnapshotCalls);

            var refreshed = await session.RefreshFromVgsAsync(_ => true);
            Assert.True(refreshed.Ok, refreshed.Message);
            Assert.Equal(PlannerMessages.Refreshed, session.StatusMessage);
            Assert.False(session.HasPendingChanges);
            Assert.False(session.CanUndo);
            Assert.Equal(1, bridge.SnapshotCalls);
        }

        [Fact]
        public async Task FailedRefreshKeepsThePlan()
        {
            var (session, bridge) = Live();
            session.MoveActivity("A100", 1);
            session.SelectedActivityId = "B200";
            bridge.SnapshotError = new StpBridgeException(StpBridgeErrorKind.SnapshotUnavailable, "snapshot unavailable", 409);
            var outcome = await session.RefreshFromVgsAsync();
            Assert.False(outcome.Ok);
            Assert.Contains("El plan local se conservó", outcome.Message);
            Assert.True(session.HasPendingChanges);
            Assert.True(session.CanUndo);
            Assert.Equal("B200", session.SelectedActivityId);

            bridge.SnapshotError = new StpBridgeException(StpBridgeErrorKind.Timeout, "timeout");
            Assert.Equal("El puente local no respondió a tiempo. Intente de nuevo. El plan local se conservó.", (await session.RefreshFromVgsAsync()).Message);
        }

        [Fact]
        public void OpenReadsThePluginSession()
        {
            string folder = Path.Combine(Path.GetTempPath(), "stp-planner-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string snapshotPath = Path.Combine(folder, "snapshot_v2.json");
                File.Copy(Path.Combine(Golden.Fixtures, "chain_fs.snapshot.json"), snapshotPath);
                var config = Golden.StaticConfig().ToJson();
                File.WriteAllText(Path.Combine(folder, PluginSession.FileName), new JObject { ["snapshot_path"] = snapshotPath, ["bridge_config"] = config }.ToString());
                var storage = new FileHorizonStorage(Path.Combine(folder, "prefs", "horizon.json"));
                using (var session = PlannerSession.Open(PluginSession.Read(folder), storage))
                {
                    Assert.Equal(4, session.Counters.Activities);
                    Assert.False(session.IsLive);
                    Assert.True(session.SetDuration(10).Ok);
                    Assert.True(session.SaveRangePreference().Ok);
                }

                using (var reopened = PlannerSession.Open(PluginSession.Read(folder), new FileHorizonStorage(Path.Combine(folder, "prefs", "horizon.json"))))
                {
                    Assert.Equal(10, reopened.DurationDays); // the preference survived in the file
                }
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private sealed class FakeBridge : IPlannerBridge
        {
            private readonly SnapshotV2 snapshot;

            public FakeBridge(SnapshotV2 snapshot) => this.snapshot = snapshot;

            public List<ApplyPackage> Submitted { get; } = new List<ApplyPackage>();

            public StpBridgeException SubmitError { get; set; }

            public StpBridgeException SnapshotError { get; set; }

            public int SnapshotCalls { get; private set; }

            public async Task<SnapshotV2> GetSnapshotAsync(SnapshotV2 current, CancellationToken cancellationToken)
            {
                await Task.Yield();
                SnapshotCalls++;
                if (SnapshotError != null) throw SnapshotError;
                return snapshot;
            }

            public async Task<ApplyPackageSaved> SubmitApplyPackageAsync(ApplyPackage package, CancellationToken cancellationToken)
            {
                await Task.Yield();
                if (SubmitError != null) throw SubmitError;
                Submitted.Add(package);
                return new ApplyPackageSaved(package.ComputeSha256(), package.ProjectFingerprint);
            }
        }
    }
}
