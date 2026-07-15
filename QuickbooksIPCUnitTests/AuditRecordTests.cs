using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ExcelAddIn1.Audit;

namespace QuickbooksIPCUnitTests
{
    [TestClass]
    public class AuditRecordTests
    {
        [TestMethod]
        public void ComputeSha256_IsStableLowercaseHex()
        {
            string hash = AuditRecord.ComputeSha256(Encoding.UTF8.GetBytes("abc"));
            Assert.AreEqual(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                hash);
        }

        [TestMethod]
        public void BuildBaseName_SlugifiesCustomerAndStampsTime()
        {
            string name = AuditRecord.BuildBaseName(
                new DateTime(2026, 7, 1, 14, 32, 10), "Acme Inc.", "chooker");
            Assert.AreEqual("20260701-143210_acme_inc_chooker", name);
        }

        [TestMethod]
        public void ProvenanceEntry_RoundTripsThroughRow()
        {
            var entry = new ProvenanceEntry
            {
                Sha256 = "deadbeef",
                OriginalPath = @"C:\q.xlsm",
                CapturedAtUtc = "2026-07-01T14:10:02Z",
                SavedAtCapture = true,
                Recalced = true,
                Origin = "create"
            };
            ProvenanceEntry back = ProvenanceEntry.FromRow(entry.ToRow());
            Assert.AreEqual(entry.Sha256, back.Sha256);
            Assert.AreEqual(entry.OriginalPath, back.OriginalPath);
            Assert.AreEqual(entry.Origin, back.Origin);
            Assert.IsTrue(back.SavedAtCapture);
            Assert.IsTrue(back.Recalced);
        }

        [TestMethod]
        public void ProvenanceEntry_ParsesExcelCoercedBooleans()
        {
            // Excel turns the written "TRUE"/"FALSE" strings into real booleans,
            // so ReadProvenance hands FromRow ".NET" casing ("True"/"False").
            ProvenanceEntry back = ProvenanceEntry.FromRow(
                new[] { "abc", "", "2026-07-01T00:00:00Z", "True", "True", "create" });
            Assert.IsTrue(back.SavedAtCapture);
            Assert.IsTrue(back.Recalced);

            ProvenanceEntry off = ProvenanceEntry.FromRow(
                new[] { "abc", "", "2026-07-01T00:00:00Z", "False", "False", "add" });
            Assert.IsFalse(off.SavedAtCapture);
            Assert.IsFalse(off.Recalced);
        }

        [TestMethod]
        public void BuildSidecarJson_EmitsSchemaAndSources()
        {
            var sources = new List<ProvenanceEntry>
            {
                new ProvenanceEntry { Sha256 = "abc123", Origin = "create",
                    CapturedAtUtc = "2026-07-01T14:10:02Z", SavedAtCapture = true, Recalced = true }
            };
            var sentLines = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> {
                    { "number", "1-4501" }, { "description", "BB/123, bend block" },
                    { "quantity", 1 }, { "rate", 250.0 }, { "override_number", "" } }
            };
            string json = AuditRecord.BuildSidecarJson(
                "2026-07-01T18:32:10Z", "EST-PC", "chooker", "1.0.0",
                "base.xlsx", "Q.xlsx", sources,
                "Acme", "PO-7", "2026-07-15", "Estimate", "26-1042", "standard",
                sentLines, 0, "OK", "txn-10231", null);

            StringAssert.Contains(json, "\"schema_version\":2");
            StringAssert.Contains(json, "\"sha256\":\"abc123\"");
            StringAssert.Contains(json, "\"origin\":\"create\"");
            StringAssert.Contains(json, "\"status_code\":0");
            StringAssert.Contains(json, "\"transaction_type\":\"Estimate\"");
            StringAssert.Contains(json, "\"quote_reference\":\"26-1042\"");
            StringAssert.Contains(json, "\"quote_family\":\"standard\"");
            StringAssert.Contains(json, "\"transaction_id\":\"txn-10231\"");
            StringAssert.Contains(json, "\"reference\":\"txn-10231\"");
        }

        [TestMethod]
        public void BuildSidecarJson_RecordsErrorOnFailedSend()
        {
            string json = AuditRecord.BuildSidecarJson(
                "2026-07-01T18:32:10Z", "EST-PC", "chooker", "1.0.0",
                "", "Q.xlsx", new List<ProvenanceEntry>(),
                "Acme", "", "2026-07-15", "Estimate", "", "",
                new List<Dictionary<string, object>>(),
                null, null, null, "QuickBooks not reachable");

            StringAssert.Contains(json, "\"error\":\"QuickBooks not reachable\"");
            StringAssert.Contains(json, "\"status_code\":null");
        }

        [TestMethod]
        public void AppendSourceCaptureJson_CreatesFirstEntry()
        {
            string json = AuditRecord.AppendSourceCaptureJson(
                null, "2026-07-02T15:04:05Z", "EST-PC", "chooker",
                "create", @"C:\quotes\q.xls");

            StringAssert.Contains(json, "\"schema_version\":1");
            StringAssert.Contains(json, "\"captured_at_utc\":\"2026-07-02T15:04:05Z\"");
            StringAssert.Contains(json, "\"windows_user\":\"chooker\"");
            StringAssert.Contains(json, "\"origin\":\"create\"");
            StringAssert.Contains(json, "\"original_path\":\"C:\\\\quotes\\\\q.xls\"");
        }

        [TestMethod]
        public void AppendSourceCaptureJson_AppendsWithoutLosingPriorCaptures()
        {
            string first = AuditRecord.AppendSourceCaptureJson(
                null, "2026-07-02T15:04:05Z", "EST-PC", "chooker", "create", "");
            string second = AuditRecord.AppendSourceCaptureJson(
                first, "2026-07-02T16:20:00Z", "SALES-PC", "jsmith", "prepare", "");

            StringAssert.Contains(second, "\"windows_user\":\"chooker\"");
            StringAssert.Contains(second, "\"windows_user\":\"jsmith\"");
            StringAssert.Contains(second, "\"origin\":\"prepare\"");
        }

        [TestMethod]
        public void AppendSourceCaptureJson_SurvivesCorruptExisting()
        {
            string json = AuditRecord.AppendSourceCaptureJson(
                "{not json", "2026-07-02T15:04:05Z", "EST-PC", "chooker", "add", "");

            StringAssert.Contains(json, "\"windows_user\":\"chooker\"");
            StringAssert.Contains(json, "\"origin\":\"add\"");
        }

        [TestMethod]
        public void IsLegacyBiff_TrueForOle2CompoundFile()
        {
            // .xls (BIFF8) files are OLE2 compound documents.
            byte[] ole2 = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00 };
            Assert.IsTrue(AuditRecord.IsLegacyBiff(ole2));
        }

        [TestMethod]
        public void IsLegacyBiff_FalseForZipPackage()
        {
            // .xlsx/.xlsm files are zip packages ("PK\x03\x04").
            byte[] zip = { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00 };
            Assert.IsFalse(AuditRecord.IsLegacyBiff(zip));
        }

        [TestMethod]
        public void IsLegacyBiff_FalseForShortOrEmptyInput()
        {
            Assert.IsFalse(AuditRecord.IsLegacyBiff(null));
            Assert.IsFalse(AuditRecord.IsLegacyBiff(new byte[0]));
            Assert.IsFalse(AuditRecord.IsLegacyBiff(new byte[] { 0xD0, 0xCF, 0x11 }));
        }
    }
}
