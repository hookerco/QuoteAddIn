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
using System.Windows.Forms;
using Button = Microsoft.Office.Tools.Excel.Controls.Button;

namespace ExcelAddIn1
{
	public class NotPartException : Exception
	{
		public NotPartException() { }
	}

	internal class SendWorksheet
	{
		private int nextRow = 0;
		Excel.Worksheet sendSheet;
		private Button sendButton;
		private List<string[]> _itemList;

		internal SendWorksheet(string customer)
		{
			sendSheet = Globals.ThisAddIn.Application.Worksheets.Add() as Excel.Worksheet;

			sendSheet.Name = "Send Quote";
			sendSheet.Cells[1, 1] = customer;
			sendSheet.Cells[2, 1] = "Number";
			sendSheet.Cells[2, 2] = "Description";
			sendSheet.Cells[2, 3] = "Quantity";
			sendSheet.Cells[2, 4] = "Price";
			sendSheet.Cells[2, 5] = "# Override";
			sendSheet.Cells[2, 6] = "IsNew";

			nextRow = 3;
		}

		internal void AddItem(string num, string desc, int quantity, double price, bool isNew)
		{
			sendSheet.Cells[nextRow, 1].Value = num;
			sendSheet.Cells[nextRow, 2].Value = desc;
			sendSheet.Cells[nextRow, 3].Value = quantity;
			sendSheet.Cells[nextRow, 4].Value = price;
			sendSheet.Cells[nextRow, 6].Value = isNew ? "Y" : "F";

			nextRow++;
		}

		internal void ConvertSheet(Excel.Worksheet oldSheet, ref List<string[]> itemList)
		{
			_itemList = itemList;
			sendSheet.Columns[1].NumberFormat = "@";
			int row = 22;
			string colA = oldSheet.Cells[row, 1].Text;
			NumberGenerator genNum = new NumberGenerator(_itemList);

			while (!colA.Contains("Total"))
			{

				Excel.Range numRange = oldSheet.Range["A" + row];
				Excel.Range descRange = oldSheet.Range["B" + row];
				Excel.Range quantRange = oldSheet.Range["F" + row];
				Excel.Range priceRange = oldSheet.Range["G" + row];

				string QBPartNum = "";
				string desc = descRange.Value;
				bool isNew = false;

				if (colA is string && colA.Contains("#") && quantRange.Text != "0")
				{
					try
					{
						string QuotePartNum = FindPN(descRange.Text);

						(QBPartNum, desc) = AllItemList.FindPart(QuotePartNum, ref itemList);
						if (QBPartNum == "")
						{
							isNew = true;
							desc = descRange.Value;
							QBPartNum = genNum.Generate();
						}

						int quant = (int)quantRange.Value;
						double price = priceRange.Value;

						AddItem(QBPartNum, desc, quant, price, isNew);
					}
					catch (NotPartException ex) { }

				}

				colA = oldSheet.Cells[++row, 1].Text;
			}

			AddCloseButton();
		}

		// Input is string, will find part number. Part number is the text before first comma. Rejects any value with length under 6. Why?
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

		internal void AddButtons()
		{
			AddCloseButton();
			AddSendButton();
		}

		internal void AddCloseButton()
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

		internal void AddSendButton() { }
	}

	internal class NumberGenerator
	{
		SortedSet<int> sortedList = new SortedSet<int>();

		internal NumberGenerator(List<string[]> itemList)
		{
			foreach (string[] item in itemList)
			{
				string partNumString = item[0];
				string pattern = @"^1-(?<number>\d+).*?";
				Match match = Regex.Match(partNumString, pattern);

				if (match.Success)
				{
					partNumString = match.Groups["number"].Value;
					int partNum = int.Parse(partNumString);
					sortedList.Add(partNum);
				}
			}
		}

		internal string Generate()
		{
			int count = 0;
			foreach (int partNum in sortedList)
			{
				if (count != partNum)
				{
					sortedList.Add(count);
					return "1-" + count.ToString("D4");
				}
				count++;
			}
			sortedList.Add(sortedList.Count);
			return "1-" + sortedList.Count.ToString("D4");
		}
	}
}
