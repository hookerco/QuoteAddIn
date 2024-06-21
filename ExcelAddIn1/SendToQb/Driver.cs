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
				Connection conn = SetConnection();

				Regex rgx = new Regex(@"- (?<customer>.+)$");
				Match mtch = rgx.Match(worksheet.Range["B11"].Text);
				string customer = mtch.Groups["customer"].Value;

				try
				{
					customer = Requests.QueryCustomer(conn, customer);
				}
				catch
				{
					conn.Close();
					MessageBox.Show("Could not complete QuickBooks request");
				}

				conn.Close();
				SalesOrderWorksheet sendSheet = new SalesOrderWorksheet(customer, worksheet);
				sendSheet.ConvertSheet();
			}
			else
			{
				MessageBox.Show("Please run when on a quote");
			}
			
		}
		
		private static Connection SetConnection()
		{
			Connection conn = new Connection();

			conn.File = "";
			if (!Properties.Settings.Default.UseActiveQuickbook)
			{
				conn.File = Properties.Settings.Default.QuickbooksPath;
			}

			try
			{
				conn.Open();
			}
			catch (Exception)
			{ // Cleanly close session and connection 
				conn.Close();
				return null;
			}

			return conn;
		}

	}
}
