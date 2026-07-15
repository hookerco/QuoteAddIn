using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Office.Tools.Excel;
using Excel = Microsoft.Office.Interop.Excel;
using System.Text.RegularExpressions;

namespace ExcelAddIn1
{
	internal static class Driver
	{
		internal static void Run()
		{

			Excel.Worksheet worksheet = Globals.ThisAddIn.Application.ActiveSheet;

			if (worksheet.Range["C1"].Text == "BEND TOOLING INC.")
			{
				Regex rgx = new Regex(@"- (?<customer>\d\d\d\d\d)$");
				Match mtch = rgx.Match(worksheet.Range["B11"].Text);
				string customer = mtch.Groups["customer"].Value;

				try
				{
					customer = Requests.QueryCustomer(customer);
				}
				catch (Exception ex)
				{
					MessageBox.Show($"Could not find customer \"{customer}\" in QuickBooks or connect to quickbooks\n\n" +
						ex.Message + "\n\n" + ex.StackTrace);
					 
					customer = "Customer not found";
				}

				// AUDIT: capture the source quote at "Prepare for Sales Order" -- the
				// reliable point of send-intent, independent of QuickBooks and the
				// customer-name / Send-button path. Writes to the shared audit folder.
				try
				{
					Excel.Workbook sourceBook = worksheet.Parent as Excel.Workbook;
					string quoteReference = ExcelAddIn1.Audit.QuoteAuditLog.ReadQuoteReference(sourceBook);
					string quoteFamily = ExcelAddIn1.Audit.QuoteAuditLog.QuoteFamily(worksheet);
					var auditSources = ExcelAddIn1.Audit.QuoteAuditLog.ReadProvenance(sourceBook);
					if (ExcelAddIn1.Audit.QuoteAuditLog.IsFullRoundWorkbook(sourceBook))
					{
						var entry = ExcelAddIn1.Audit.QuoteAuditLog.SnapshotWorkbook(sourceBook, "prepare");
						if (entry != null && !auditSources.Exists(s => s.Sha256 == entry.Sha256))
							auditSources.Add(entry);
					}
					else if (auditSources.Count == 0)
					{
						// AUDIT: aux books created before the audit feature carry no
						// provenance sheet; snapshot the aux book itself so the send
						// still captures what was actually sent.
						var entry = ExcelAddIn1.Audit.QuoteAuditLog.SnapshotWorkbook(sourceBook, "prepare_aux");
						if (entry != null) auditSources.Add(entry);
					}
					ExcelAddIn1.Audit.QuoteAuditLog.WriteSendRecord(
						sourceBook, auditSources, null, customer, "", "", "",
						quoteReference, quoteFamily, null, "");
				}
				catch { }

				try
				{
					SalesOrderWorksheet sendSheet = new SalesOrderWorksheet(customer, worksheet);
					sendSheet.ConvertSheet();
				}
				catch (Exception ex)
				{
					MessageBox.Show(ex.Message + "\n\n" + ex.StackTrace);
				}

			}
			else
			{
				MessageBox.Show("Please run when on a quote");
			}
		}
	}
}
