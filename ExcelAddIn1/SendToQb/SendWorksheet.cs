using Microsoft.Office.Tools.Excel.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Excel = Microsoft.Office.Interop.Excel;
using Microsoft.Office.Tools.Excel;
using System.Linq.Expressions;

namespace ExcelAddIn1
{
	public class NotPartException:Exception
	{
		public NotPartException() { }


	}
	internal class SendWorksheet
	{
		private int nextRow = 0;
		Excel.Worksheet sendSheet;
		private Button sendButton;

		internal SendWorksheet(string customer)
		{
			sendSheet = Globals.ThisAddIn.Application.Worksheets.Add();

			sendSheet.Name = "Send Quote";
			sendSheet.Cells[1, 1] = customer;
			sendSheet.Cells[2, 1] = "Number";
			sendSheet.Cells[2, 2] = "Description";
			sendSheet.Cells[2, 3] = "Quantity";
			sendSheet.Cells[2, 4] = "Price";

			nextRow = 3;
		}

		internal void AddItem(string num, string desc, int quantity, double price)
		{
			sendSheet.Cells[nextRow, 1].Value = num;
			sendSheet.Cells[nextRow, 2].Value = desc;
			sendSheet.Cells[nextRow, 3].Value = quantity;
			sendSheet.Cells[nextRow, 4].Value = price;

			nextRow++;
		}

		internal void ConvertSheet(Excel.Worksheet oldSheet, List<string[]> itemList)
		{
			sendSheet.Columns[1].NumberFormat = "@";
			int row = 22;
			string colA = oldSheet.Cells[row, 1].Text;

			while (!colA.Contains("Total"))
			{

				Excel.Range numRange = oldSheet.Range["A"+row];
				Excel.Range descRange = oldSheet.Range["B"+row];
				Excel.Range quantRange = oldSheet.Range["F"+row];
				Excel.Range priceRange = oldSheet.Range["G"+row];

				string QBPartNum = "";
				string desc = descRange.Value;

				if (colA is string && colA.Contains("#")) {
					try
					{
						string QuotePartNum = FindPN(descRange.Text);
						
						(QBPartNum, desc) = AllItemList.FindPart(QuotePartNum, itemList);
						if (QBPartNum == "")
						{
							desc = descRange.Value;
						}

						int quant = (int)quantRange.Value;
						double price = priceRange.Value;

						AddItem(QBPartNum, desc, quant, price);
					}
					catch (NotPartException ex) { }

				}

				colA = oldSheet.Cells[++row, 1].Text;
			}

			AddButton();
		}

		// Finds ALL text after "BTI p/n"
		// Input is string, will find any string after 'BTI p/n' until hits whitespace (.) Rejects any value with length under 6. Why?
		internal static string FindPN(string desc)
		{
			string pattern = @"^(?<partNumber>.*?),";
			Match match = Regex.Match(desc, pattern);
			if (match.Success)
			{
				return match.Groups["partNumber"].Value;
			}
			throw new NotPartException();
		}

		internal void AddButton()
		{
			sendButton = new Button();

			sendButton.Text = "Close";
			sendButton.Click += (sender, e) =>
			{
				Globals.ThisAddIn.Application.DisplayAlerts = false;
				sendSheet.Delete();
				Globals.ThisAddIn.Application.DisplayAlerts = true;
			};

			Worksheet sheet = Globals.Factory.GetVstoObject(sendSheet);

			Excel.Range range = sheet.Range["A" + nextRow];
			sheet.Controls.AddControl(sendButton, range, "sendButton");
		}
	}
}
