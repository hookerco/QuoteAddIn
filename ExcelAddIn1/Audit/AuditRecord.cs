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
            string transactionType, string quoteReference,
            IEnumerable<Dictionary<string, object>> sentLines,
            object statusCode, string statusMessage, string reference,
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
                { "schema_version", 1 },
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
                    { "quote_reference", quoteReference } } },
                { "sent_lines", sentList },
                { "quickbooks_response", new Dictionary<string, object> {
                    { "status_code", statusCode }, { "status_message", statusMessage },
                    { "reference", reference },
                    { "error", string.IsNullOrEmpty(errorMessage) ? null : errorMessage } } },
            };
            return new JavaScriptSerializer().Serialize(root);
        }
    }
}
