using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Excel = Microsoft.Office.Interop.Excel;
using Office = Microsoft.Office.Core;
using QuickBooksIPCContracts;

namespace ExcelAddIn1.Audit
{
    /// <summary>
    /// Best-effort audit capture for QuickBooks sends. Snapshots source quote
    /// workbooks into a content-addressed pool on a shared folder at Create/Add,
    /// records provenance on the aux workbook, and drops a sidecar record at
    /// Send. Every method swallows its own failures: audit must never block or
    /// alter a QuickBooks send.
    /// </summary>
    internal static class QuoteAuditLog
    {
        private const string EnvVar = "QUOTE_AUDIT_DIR";
        private const string DefaultRoot =
            @"\\PC-VS-APPFS01\CNC Process\COLTON TEST\QuoteEngine\audits";
        private const string ProvenanceSheet = "_QuoteAuditProvenance";
        private const string AddinVersion = "1.0.0";

        internal static string ResolveAuditRoot()
        {
            string root = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrWhiteSpace(root)) root = DefaultRoot;
            Directory.CreateDirectory(Path.Combine(root, "sources"));
            Directory.CreateDirectory(Path.Combine(root, "sends"));
            return root;
        }

        // Snapshot an open workbook: force recalc, SaveCopyAs to temp, hash,
        // write into the content-addressed pool. Returns null on any failure.
        internal static ProvenanceEntry SnapshotWorkbook(Excel.Workbook wb, string origin)
        {
            try
            {
                try { wb.Application.CalculateFull(); } catch { /* best effort */ }
                string temp = Path.Combine(Path.GetTempPath(),
                    "qa_" + Guid.NewGuid().ToString("N") + ".xlsm");
                wb.SaveCopyAs(temp);
                byte[] bytes = File.ReadAllBytes(temp);
                try { File.Delete(temp); } catch { }
                // SaveCopyAs preserves the source's native format, so a legacy
                // .xls comes back as BIFF bytes openpyxl can't replay. Export
                // through Excel to a real .xlsm before pooling.
                if (AuditRecord.IsLegacyBiff(bytes))
                    bytes = ConvertLegacyToXlsm(wb.Application, bytes) ?? bytes;
                bool saved = false;
                string path = "";
                try { path = wb.FullName; saved = wb.Saved && !string.IsNullOrEmpty(path); }
                catch { }
                return PoolWrite(bytes, saved ? path : "", saved, true, origin);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("audit SnapshotWorkbook: " + ex);
                return null;
            }
        }

        internal static ProvenanceEntry SnapshotFile(string filePath, Excel.Application app)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                if (AuditRecord.IsLegacyBiff(bytes) && app != null)
                    bytes = ConvertLegacyToXlsm(app, bytes) ?? bytes;
                return PoolWrite(bytes, filePath, true, false, "add");
            }
            catch (Exception ex)
            {
                Trace.WriteLine("audit SnapshotFile: " + ex);
                return null;
            }
        }

        // Export legacy BIFF (.xls) bytes to .xlsm via the running Excel:
        // round-trip through temp files, open the copy with macros/events off,
        // SaveAs xlOpenXMLWorkbookMacroEnabled. Cached values survive; the
        // Python replay side stays openpyxl-only. Returns null on any failure
        // (caller falls back to pooling the raw bytes).
        private static byte[] ConvertLegacyToXlsm(Excel.Application app, byte[] biffBytes)
        {
            string tempXls = Path.Combine(Path.GetTempPath(),
                "qa_" + Guid.NewGuid().ToString("N") + ".xls");
            string tempXlsm = Path.ChangeExtension(tempXls, ".xlsm");
            bool alerts = true, events = true;
            Office.MsoAutomationSecurity security =
                Office.MsoAutomationSecurity.msoAutomationSecurityByUI;
            try
            {
                alerts = app.DisplayAlerts;
                events = app.EnableEvents;
                security = app.AutomationSecurity;
                app.DisplayAlerts = false;
                app.EnableEvents = false;
                app.AutomationSecurity =
                    Office.MsoAutomationSecurity.msoAutomationSecurityForceDisable;

                File.WriteAllBytes(tempXls, biffBytes);
                Excel.Workbook copy = app.Workbooks.Open(tempXls, UpdateLinks: 0);
                try
                {
                    copy.SaveAs(tempXlsm,
                        Excel.XlFileFormat.xlOpenXMLWorkbookMacroEnabled);
                }
                finally
                {
                    copy.Close(false);
                }
                return File.ReadAllBytes(tempXlsm);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("audit ConvertLegacyToXlsm: " + ex);
                return null;
            }
            finally
            {
                try { app.DisplayAlerts = alerts; } catch { }
                try { app.EnableEvents = events; } catch { }
                try { app.AutomationSecurity = security; } catch { }
                try { if (File.Exists(tempXls)) File.Delete(tempXls); } catch { }
                try { if (File.Exists(tempXlsm)) File.Delete(tempXlsm); } catch { }
            }
        }

        private static ProvenanceEntry PoolWrite(
            byte[] bytes, string originalPath, bool saved, bool recalced, string origin)
        {
            string root = ResolveAuditRoot();
            string sha = AuditRecord.ComputeSha256(bytes);
            string capturedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string blob = Path.Combine(root, "sources", sha + ".xlsm");
            if (!File.Exists(blob)) File.WriteAllBytes(blob, bytes);
            try
            {
                string meta = Path.Combine(root, "sources", sha + ".meta.json");
                string existing = File.Exists(meta) ? File.ReadAllText(meta) : null;
                File.WriteAllText(meta, AuditRecord.AppendSourceCaptureJson(
                    existing, capturedAt, Environment.MachineName,
                    Environment.UserName, origin, originalPath));
            }
            catch (Exception ex) { Trace.WriteLine("audit PoolWrite meta: " + ex); }
            return new ProvenanceEntry
            {
                Sha256 = sha,
                OriginalPath = originalPath ?? "",
                CapturedAtUtc = capturedAt,
                SavedAtCapture = saved,
                Recalced = recalced,
                Origin = origin
            };
        }

        internal static List<ProvenanceEntry> ReadProvenance(Excel.Workbook wb)
        {
            var entries = new List<ProvenanceEntry>();
            try
            {
                Excel.Worksheet sheet = FindSheet(wb, ProvenanceSheet);
                if (sheet == null) return entries;
                Excel.Range used = sheet.UsedRange;
                int rows = used.Rows.Count;
                for (int r = 1; r <= rows; r++)
                {
                    var row = new string[6];
                    for (int c = 0; c < 6; c++)
                    {
                        object v = (sheet.Cells[r, c + 1] as Excel.Range).Value2;
                        row[c] = v == null ? "" : Convert.ToString(v);
                    }
                    if (!string.IsNullOrEmpty(row[0])) entries.Add(ProvenanceEntry.FromRow(row));
                }
            }
            catch (Exception ex) { Trace.WriteLine("audit ReadProvenance: " + ex); }
            return entries;
        }

        internal static void AppendProvenance(Excel.Workbook wb, ProvenanceEntry entry)
        {
            if (entry == null || wb == null) return;
            try
            {
                Excel.Worksheet sheet = FindSheet(wb, ProvenanceSheet) ?? CreateHiddenSheet(wb);
                int next = 1;
                object first = (sheet.Cells[1, 1] as Excel.Range).Value2;
                if (first != null && Convert.ToString(first) != "")
                    next = sheet.UsedRange.Rows.Count + 1;
                string[] row = entry.ToRow();
                for (int c = 0; c < row.Length; c++)
                    (sheet.Cells[next, c + 1] as Excel.Range).Value2 = row[c];
            }
            catch (Exception ex) { Trace.WriteLine("audit AppendProvenance: " + ex); }
        }

        private static Excel.Worksheet CreateHiddenSheet(Excel.Workbook wb)
        {
            // Parameterless Add() inserts before the active sheet, which would
            // put this sheet at index 1 ahead of the quote sheet.
            Excel.Worksheet sheet = wb.Worksheets.Add(
                After: wb.Worksheets[wb.Worksheets.Count]);
            sheet.Name = ProvenanceSheet;
            sheet.Visible = Excel.XlSheetVisibility.xlSheetVeryHidden;
            return sheet;
        }

        private static Excel.Worksheet FindSheet(Excel.Workbook wb, string name)
        {
            foreach (Excel.Worksheet s in wb.Worksheets)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        internal static bool IsFullRoundWorkbook(Excel.Workbook wb)
        {
            return FindSheet(wb, "Standard RFQ") != null
                && FindSheet(wb, "Calculations") != null;
        }

        internal static void WriteSendRecord(
            Excel.Workbook auxBook, List<ProvenanceEntry> sources,
            IEnumerable<Dictionary<string, object>> sentLines,
            string customer, string po, string dueDate, string txnType,
            string quoteReference, QBStatusResponse<string> response,
            string errorMessage)
        {
            try
            {
                string root = ResolveAuditRoot();
                string sendsDir = Path.Combine(root, "sends");
                DateTime now = DateTime.Now;
                string baseName = UniqueBaseName(sendsDir,
                    AuditRecord.BuildBaseName(now, customer, Environment.UserName));

                // sends/ holds only the JSON record; the workbook lives once in
                // sources/ (referenced by the source hashes below). No duplicate
                // .xlsx copy here.
                string json = AuditRecord.BuildSidecarJson(
                    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    Environment.MachineName, Environment.UserName, AddinVersion,
                    "", SafeName(auxBook),
                    sources, customer, po, dueDate, txnType, quoteReference, sentLines,
                    response == null ? (object)null : response.StatusCode,
                    response == null ? null : response.StatusMessage,
                    response == null ? null : response.Data,
                    errorMessage);
                File.WriteAllText(Path.Combine(sendsDir, baseName + ".json"), json);
            }
            catch (Exception ex) { Trace.WriteLine("audit WriteSendRecord: " + ex); }
        }

        private static string SafeName(Excel.Workbook wb)
        {
            try { return wb.Name; } catch { return ""; }
        }

        private static string UniqueBaseName(string sendsDir, string baseName)
        {
            string candidate = baseName;
            int n = 2;
            while (File.Exists(Path.Combine(sendsDir, candidate + ".json")))
                candidate = baseName + "-" + (n++);
            return candidate;
        }
    }
}
