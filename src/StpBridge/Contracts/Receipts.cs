using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Maptek.Vulcan.Stp.Bridge.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Bridge.Contracts
{
    /// <summary>
    /// 201 body of <c>POST /apply-package</c>:
    /// <c>{"status":"saved","schema":"vgs-stp-apply/1","package_sha256":…,"project_fingerprint":…}</c>.
    /// </summary>
    public sealed class ApplyPackageSaved
    {
        public ApplyPackageSaved(string packageSha256, string projectFingerprint)
        {
            if (!WireJson.IsLowerHex(packageSha256, 64)) throw new WireFormatException("package_sha256 must be 64 lowercase hex characters");
            PackageSha256 = packageSha256;
            ProjectFingerprint = ApplyPackage.NonEmpty(projectFingerprint, "project_fingerprint");
        }

        public string Status => "saved";

        public string Schema => ApplyPackage.SchemaId;

        public string PackageSha256 { get; }

        public string ProjectFingerprint { get; }

        public static ApplyPackageSaved FromJson(JToken token)
        {
            const string L = "apply receipt";
            var raw = WireJson.ExactObject(token, L, "status", "schema", "package_sha256", "project_fingerprint");
            WireJson.RequireConstant(WireJson.Text(raw, "status", L), "saved", "apply receipt status");
            WireJson.RequireConstant(WireJson.Text(raw, "schema", L), ApplyPackage.SchemaId, "apply receipt schema");
            return new ApplyPackageSaved(WireJson.String(raw, "package_sha256", L), WireJson.Text(raw, "project_fingerprint", L));
        }

        public JObject ToJson() => new JObject
        {
            ["status"] = Status,
            ["schema"] = Schema,
            ["package_sha256"] = PackageSha256,
            ["project_fingerprint"] = ProjectFingerprint,
        };
    }

    /// <summary>
    /// <c>vgs-stp-apply-ready/1</c> — <c>Respaldos_STPUG-VGS_v2/stp_apply_pending_v2.ready.json</c>, the digest-bound
    /// readiness receipt the server writes beside the package (script 3 checks it).
    /// </summary>
    public sealed class ApplyReadyReceipt
    {
        public const string SchemaId = "vgs-stp-apply-ready/1";

        public ApplyReadyReceipt(string packageFile, string packageSha256, string projectFingerprint)
        {
            PackageFile = ApplyPackage.NonEmpty(packageFile, "package_file");
            if (!WireJson.IsLowerHex(packageSha256, 64)) throw new WireFormatException("package_sha256 must be 64 lowercase hex characters");
            PackageSha256 = packageSha256;
            ProjectFingerprint = ApplyPackage.NonEmpty(projectFingerprint, "project_fingerprint");
        }

        public string Schema => SchemaId;

        public string PackageFile { get; }

        public string PackageSha256 { get; }

        public string ProjectFingerprint { get; }

        public static ApplyReadyReceipt Parse(string json) => FromJson(WireJson.Parse(json));

        public static ApplyReadyReceipt FromJson(JToken token)
        {
            const string L = "apply ready receipt";
            var raw = WireJson.ExactObject(token, L, "schema", "package_file", "package_sha256", "project_fingerprint");
            WireJson.RequireConstant(WireJson.Text(raw, "schema", L), SchemaId, "apply ready schema");
            return new ApplyReadyReceipt(WireJson.Text(raw, "package_file", L), WireJson.String(raw, "package_sha256", L), WireJson.Text(raw, "project_fingerprint", L));
        }

        public JObject ToJson() => new JObject
        {
            ["schema"] = Schema,
            ["package_file"] = PackageFile,
            ["package_sha256"] = PackageSha256,
            ["project_fingerprint"] = ProjectFingerprint,
        };
    }

    /// <summary>
    /// <c>vgs-stp-pending-save/1</c> — <c>Back up STP/reporte stp/*.pending.json</c>, written by script 3 after a
    /// successful apply and read by script 4. Port of <c>_pending_save_receipt_from_value</c>.
    /// The file on disk is the canonical JSON plus one trailing <c>\n</c>.
    /// </summary>
    public sealed class PendingSaveReceipt
    {
        public const string SchemaId = "vgs-stp-pending-save/1";

        private PendingSaveReceipt(JObject raw)
        {
            const string L = "pending receipt";
            WireJson.RequireConstant(WireJson.Text(raw, "schema", L), SchemaId, "pending receipt schema");
            ProjectName = WireJson.Text(raw, "project_name", L);
            if (Path.GetFileName(ProjectName) != ProjectName || !ProjectName.EndsWith(".vgantt", StringComparison.OrdinalIgnoreCase))
                throw new WireFormatException("pending receipt project name is invalid");
            PackageSha256 = Digest(raw, "package_sha256");
            BackupPath = WireJson.String(raw, "backup_path", L);
            if (!BackupPath.EndsWith(".vgantt", StringComparison.OrdinalIgnoreCase)) throw new WireFormatException("pending receipt backup path is invalid");
            BackupSha256 = Digest(raw, "backup_sha256");
            ProjectFingerprint = Fingerprint(raw, "project_fingerprint");
            PrecedenceFingerprint = Fingerprint(raw, "precedence_fingerprint");
            var ids = WireJson.List(raw, "authorized_ids", L);
            if (ids.Count == 0 || ids.Any(t => t.Type != JTokenType.String || ((string)t).Length == 0))
                throw new WireFormatException("pending receipt authorized ids are invalid");
            AuthorizedIds = ids.Select(t => (string)t).ToList().AsReadOnly();
            if (AuthorizedIds.Distinct(StringComparer.Ordinal).Count() != AuthorizedIds.Count)
                throw new WireFormatException("pending receipt authorized ids are invalid");
            NativeOperationCost = new NativeOperationCost(raw["native_operation_cost"]);
        }

        public string Schema => SchemaId;

        public string ProjectName { get; }

        public string PackageSha256 { get; }

        public string BackupPath { get; }

        public string BackupSha256 { get; }

        public string ProjectFingerprint { get; }

        public string PrecedenceFingerprint { get; }

        public IReadOnlyList<string> AuthorizedIds { get; }

        public NativeOperationCost NativeOperationCost { get; }

        public static PendingSaveReceipt Parse(string json) => FromJson(WireJson.Parse(json));

        public static PendingSaveReceipt FromJson(JToken token) => new PendingSaveReceipt(WireJson.ExactObject(
            token, "pending receipt", "schema", "project_name", "package_sha256", "backup_path", "backup_sha256",
            "project_fingerprint", "precedence_fingerprint", "authorized_ids", "native_operation_cost"));

        public JObject ToJson() => new JObject
        {
            ["schema"] = Schema,
            ["project_name"] = ProjectName,
            ["package_sha256"] = PackageSha256,
            ["backup_path"] = BackupPath,
            ["backup_sha256"] = BackupSha256,
            ["project_fingerprint"] = ProjectFingerprint,
            ["precedence_fingerprint"] = PrecedenceFingerprint,
            ["authorized_ids"] = new JArray(AuthorizedIds.OrderBy(x => x, CodePointComparer.Instance)),
            ["native_operation_cost"] = NativeOperationCost.ToJson(),
        };

        private static string Digest(JObject raw, string key)
        {
            var value = WireJson.String(raw, key, "pending receipt");
            if (!WireJson.IsLowerHex(value, 64)) throw new WireFormatException("pending receipt digest is invalid");
            return value;
        }

        private static string Fingerprint(JObject raw, string key)
        {
            var value = WireJson.String(raw, key, "pending receipt");
            if (value.Length == 0 || value.Length > 256) throw new WireFormatException("pending receipt fingerprint is invalid");
            return value;
        }
    }

    /// <summary>Exact count of native VGS calls in a pending-save receipt (<c>NativeOperationCost</c>).</summary>
    public sealed class NativeOperationCost
    {
        internal NativeOperationCost(JToken token)
        {
            const string L = "native_operation_cost";
            var raw = WireJson.ExactObject(token, L, "split_count", "move_count", "total", "by_activity");
            SplitCount = WireJson.Integer(raw, "split_count", L);
            MoveCount = WireJson.Integer(raw, "move_count", L);
            Total = WireJson.Integer(raw, "total", L);
            if (SplitCount < 0 || MoveCount < 0 || Total != SplitCount + MoveCount) throw new WireFormatException("native operation cost is invalid");
            if (!(raw["by_activity"] is JObject byActivity)) throw new WireFormatException("native operation cost is invalid");
            var costs = new SortedDictionary<string, long>(CodePointComparer.Instance);
            foreach (var property in byActivity.Properties())
            {
                var count = WireJson.Integer(byActivity, property.Name, L + ".by_activity");
                if (property.Name.Length == 0 || count <= 0) throw new WireFormatException("native operation activity cost is invalid");
                costs[property.Name] = count;
            }
            if (costs.Values.Sum() != Total) throw new WireFormatException("native operation cost does not add up");
            ByActivity = costs;
        }

        public long SplitCount { get; }

        public long MoveCount { get; }

        public long Total { get; }

        public IReadOnlyDictionary<string, long> ByActivity { get; }

        public JObject ToJson()
        {
            var byActivity = new JObject();
            foreach (var pair in ByActivity) byActivity[pair.Key] = pair.Value;
            return new JObject
            {
                ["split_count"] = SplitCount,
                ["move_count"] = MoveCount,
                ["total"] = Total,
                ["by_activity"] = byActivity,
            };
        }
    }
}
