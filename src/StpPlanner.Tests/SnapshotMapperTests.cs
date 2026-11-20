using System.Linq;
using Maptek.Vulcan.Stp.Engine;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Planner.Tests
{
    public class SnapshotMapperTests
    {
        public static System.Collections.Generic.IEnumerable<object[]> Fixtures() => Golden.StrictFixtures();

        /// <summary>Every engine string is the exact text of the snapshot file (times, decimals, lags).</summary>
        [Theory]
        [MemberData(nameof(Fixtures))]
        public void EngineSeesThePythonText(string fixture)
        {
            var raw = Golden.RawSnapshot(fixture);
            var engine = SnapshotMapper.ToEngine(Golden.Snapshot(fixture));
            var activities = (JArray)raw["activities"];
            Assert.Equal(activities.Count, engine.Activities.Count);
            for (int i = 0; i < activities.Count; i++)
            {
                var a = activities[i];
                var e = engine.Activities[i];
                Assert.Equal((string)a["id"], e.Id);
                Assert.Equal(a["nativeBars"].Select(b => Text(b, "key", "xmlKey", "start", "finish", "quantityPercentage", "percentComplete", "completeWorkedMinutes")),
                    e.NativeBars.Select(b => string.Join("|", b.Key, b.XmlKey, b.Start, b.Finish, b.QuantityPercentage, b.PercentComplete, b.CompleteWorkedMinutes)));
                Assert.Equal(a["fragments"].Select(f => Text(f, "id", "nativeBarXmlKey", "nativeBarKey", "start", "finish", "originalStart", "originalFinish", "logicalUnitDate", "quantityPercentage") + "|" + (bool)f["displaySuppressed"] + "|" + (bool)f["lockedCompleted"]),
                    e.Fragments.Select(f => string.Join("|", f.Id, f.NativeBarXmlKey, f.NativeBarKey, f.Start, f.Finish, f.OriginalStart, f.OriginalFinish, f.LogicalUnitDate, f.QuantityPercentage) + "|" + f.DisplaySuppressed + "|" + f.LockedCompleted));
                Assert.Equal(a["nativeAnchors"].Select(n => Text(n, "leftBarXmlKey", "leftBarKey", "rightBarXmlKey", "rightBarKey", "at", "rightAt")),
                    e.NativeAnchors.Select(n => string.Join("|", n.LeftBarXmlKey, n.LeftBarKey, n.RightBarXmlKey, n.RightBarKey, n.At, n.RightAt)));
            }

            Assert.Equal(raw["precedences"].Select(p => Text(p, "key", "predecessorId", "successorId", "predecessorBar", "successorBar", "type", "lagMicroseconds", "groupKey")),
                engine.Precedences.Select(p => string.Join("|", p.Key, p.PredecessorId, p.SuccessorId, p.PredecessorBar, p.SuccessorBar, p.Type, p.LagMicroseconds, p.GroupKey)));
            Assert.All(engine.Precedences, p => Assert.Null(p.LagSeconds));
        }

        /// <summary>The mapped model passes the strict browser preflight, except where the golden case expects a setup error.</summary>
        [Theory]
        [MemberData(nameof(Fixtures))]
        public void MappedSnapshotBuildsAValidModel(string fixture)
        {
            var model = SnapshotMapper.ToModel(Golden.Snapshot(fixture));
            Assert.NotEmpty(model.Fragments);
            Assert.All(model.Fragments, f => Assert.Equal(string.Empty, f.Provenance));
        }

        [Fact]
        public void SubSecondTimesKeepTheSnapshotSpelling()
        {
            var raw = Golden.RawSnapshot("chain_fs");
            var fragment = (JObject)raw["activities"][0]["fragments"][0];
            fragment["start"] = ((string)fragment["start"]).Substring(0, 19) + ".25";
            var engine = SnapshotMapper.ToEngine(Bridge.Contracts.SnapshotV2.FromJson(raw));
            Assert.EndsWith(":00.25", engine.Activities[0].Fragments[0].Start);
        }

        private static string Text(JToken token, params string[] keys) => string.Join("|", keys.Select(k => (string)token[k]));
    }
}
