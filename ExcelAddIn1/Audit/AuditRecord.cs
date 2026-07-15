using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ExcelAddIn1.Audit
{
    /// <summary>
    /// One captured source-quote snapshot's provenance, stored as a row on the
    /// aux workbook's very-hidden _QuoteAuditProvenance sheet.
    /// </summary>
    public class ProvenanceEntry
    {
        public string Sha256 { get; set; } = "";
        public string OriginalPath { get; set; } = "";
        public string CapturedAtUtc { get; set; } = "";
        public bool SavedAtCapture { get; set; }
        public bool Recalced { get; set; }
        public string Origin { get; set; } = "";

        public string[] ToRow()
        {
            return new[]
            {
                Sha256, OriginalPath, CapturedAtUtc,
                SavedAtCapture ? "TRUE" : "FALSE",
                Recalced ? "TRUE" : "FALSE", Origin
            };
        }

        public static ProvenanceEntry FromRow(string[] row)
        {
            return new ProvenanceEntry
            {
                Sha256 = Get(row, 0),
                OriginalPath = Get(row, 1),
                CapturedAtUtc = Get(row, 2),
                SavedAtCapture = AsBool(Get(row, 3)),
                Recalced = AsBool(Get(row, 4)),
                Origin = Get(row, 5)
            };
        }

        private static string Get(string[] row, int i)
        {
            return (row != null && i < row.Length && row[i] != null) ? row[i] : "";
        }

        // Excel coerces a "TRUE"/"FALSE" string written to a cell into a real
        // boolean, so ReadProvenance reads it back as ".NET" casing ("True").
        // Parse tolerantly so the round-trip survives that coercion.
        private static bool AsBool(string s)
        {
            bool b;
            return bool.TryParse(s, out b) && b;
        }
    }

    /// <summary>
    /// Pure (Office-free) audit helpers: content hashing, filename slugs, and
    /// sidecar JSON assembly. Unit-tested via QuickbooksIPCUnitTests, which
    /// link-compiles this file (no Excel dependency).
    /// </summary>
    public static class AuditRecord
    {
        public static string ComputeSha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // OLE2 compound-document magic: legacy binary .xls (BIFF). Modern
        // .xlsx/.xlsm are zip packages ("PK\x03\x04") and won't match.
        private static readonly byte[] Ole2Magic =
            { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

        public static bool IsLegacyBiff(byte[] data)
        {
            if (data == null || data.Length < Ole2Magic.Length) return false;
            for (int i = 0; i < Ole2Magic.Length; i++)
                if (data[i] != Ole2Magic[i]) return false;
            return true;
        }

        // sources/{sha}.meta.json: who/when/why a pooled workbook was captured.
        // The blob name must stay pure content-hash (dedupe + replay lookup),
        // so user/time live in this sidecar; each capture appends an entry.
        public static string AppendSourceCaptureJson(
            string existingJson, string capturedAtUtc, string machine,
            string winUser, string origin, string originalPath)
        {
            var serializer = new JavaScriptSerializer();
            var captures = new List<object>();
            try
            {
                if (!string.IsNullOrEmpty(existingJson))
                {
                    var root = serializer.Deserialize<Dictionary<string, object>>(existingJson);
                    object prior;
                    if (root != null && root.TryGetValue("captures", out prior)
                        && prior is System.Collections.IEnumerable)
                        foreach (object item in (System.Collections.IEnumerable)prior)
                            captures.Add(item);
                }
            }
            catch { /* corrupt meta: keep the new capture, drop the rest */ }
            captures.Add(new Dictionary<string, object>
            {
                { "captured_at_utc", capturedAtUtc },
                { "machine", machine },
                { "windows_user", winUser },
                { "origin", origin },
                { "original_path", string.IsNullOrEmpty(originalPath) ? null : originalPath }
            });
            return serializer.Serialize(new Dictionary<string, object>
            {
                { "schema_version", 1 },
                { "captures", captures }
            });
        }

        public static string BuildBaseName(DateTime whenLocal, string customer, string winUser)
        {
            string ts = whenLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            return ts + "_" + Slug(customer) + "_" + Slug(winUser);
        }

        private static string Slug(string value)
        {
            string lower = (value ?? "").ToLowerInvariant();
            string cleaned = Regex.Replace(lower, "[^a-z0-9]+", "_").Trim('_');
            return cleaned.Length == 0 ? "unknown" : cleaned;
        }

        public static string BuildSidecarJson(
            string capturedAtUtc, string machine, string winUser, string addinVersion,
            string auxSavedCopy, string auxFileName,
            IEnumerable<ProvenanceEntry> sources,
            string customerName, string customerPo, string dueDate,
            string transactionType, string quoteReference, string quoteFamily,
            IEnumerable<Dictionary<string, object>> sentLines,
            object statusCode, string statusMessage, string transactionId,
            string errorMessage)
        {
            var sourceList = new List<object>();
            foreach (var s in sources ?? new List<ProvenanceEntry>())
            {
                sourceList.Add(new Dictionary<string, object>
                {
                    { "sha256", s.Sha256 },
                    { "original_path", string.IsNullOrEmpty(s.OriginalPath) ? null : s.OriginalPath },
                    { "origin", s.Origin },
                    { "captured_at_utc", s.CapturedAtUtc },
                    { "saved_at_capture", s.SavedAtCapture },
                    { "recalced", s.Recalced },
                });
            }

            var sentList = new List<object>();
            foreach (var line in sentLines ?? new List<Dictionary<string, object>>())
                sentList.Add(line);

            var root = new Dictionary<string, object>
            {
                { "schema_version", 2 },
                { "captured_at_utc", capturedAtUtc },
                { "source", new Dictionary<string, object> {
                    { "machine", machine }, { "windows_user", winUser },
                    { "addin_version", addinVersion } } },
                { "aux_book", new Dictionary<string, object> {
                    { "saved_copy", auxSavedCopy }, { "file_name", auxFileName } } },
                { "sources", sourceList },
                { "quote", new Dictionary<string, object> {
                    { "customer_name", customerName }, { "customer_po", customerPo },
                    { "due_date", dueDate }, { "transaction_type", transactionType },
                    { "quote_reference", quoteReference }, { "quote_family", quoteFamily } } },
                { "sent_lines", sentList },
                { "quickbooks_response", new Dictionary<string, object> {
                    { "status_code", statusCode }, { "status_message", statusMessage },
                    { "transaction_id", transactionId }, { "reference", transactionId },
                    { "error", string.IsNullOrEmpty(errorMessage) ? null : errorMessage } } },
            };
            return new JavaScriptSerializer().Serialize(root);
        }
    }
}
