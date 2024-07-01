using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Office.Tools.Excel;
using Excel = Microsoft.Office.Interop.Excel;
using QBRequestLibrary;
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
