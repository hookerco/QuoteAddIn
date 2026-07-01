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
                "Acme", "PO-7", "2026-07-15", "Estimate", "26-1042",
                sentLines, 0, "OK", "E-10231");

            StringAssert.Contains(json, "\"schema_version\":1");
            StringAssert.Contains(json, "\"sha256\":\"abc123\"");
            StringAssert.Contains(json, "\"origin\":\"create\"");
            StringAssert.Contains(json, "\"status_code\":0");
            StringAssert.Contains(json, "\"transaction_type\":\"Estimate\"");
        }
    }
}
