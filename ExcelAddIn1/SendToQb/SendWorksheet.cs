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
using Worksheet = Microsoft.Office.Tools.Excel.Worksheet;
using QBRequestLibrary;
using Microsoft.Office.Interop.Excel;

namespace ExcelAddIn1
{
	public class NotPartException : Exception
	{
		public NotPartException() { }
	}

	internal class SendWorksheet
	{
		private static int firstRow = 0;
		private int nextRow = 0;
		private readonly Excel.Worksheet sendSheet;
		private Button closeButton;
		private Button sendButton;
		private List<string[]> _allItemList;
		private List<SendSheetQuoteItem> _currItemList= new List<SendSheetQuoteItem>();

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
			firstRow = 3;
		}

		internal void AddItem(string num, string desc, int quantity, double price, bool isNew)
		{
			sendSheet.Cells[nextRow, 1].Value = num;
			sendSheet.Cells[nextRow, 2].Value = desc;
			sendSheet.Cells[nextRow, 3].Value = quantity;
			sendSheet.Cells[nextRow, 4].Value = price;
			sendSheet.Cells[nextRow, 6].Value = isNew ? "Y" : "N";

			nextRow++;
		}

		internal void ConvertSheet(Excel.Worksheet oldSheet, ref List<string[]> itemList)
		{
			_allItemList = itemList;
			sendSheet.Columns[1].NumberFormat = "@";
			int row = 22;
			string colA = oldSheet.Cells[row, 1].Text;
			NumberGenerator genNum = new NumberGenerator(_allItemList);

			while (!colA.Contains("Total"))
			{
				SendSheetQuoteItem newItem = new SendSheetQuoteItem(sendSheet.Range[nextRow + "1", nextRow + "6"]);

				newItem.SetNumber("");
				newItem.SetDescription(oldSheet.Range["B" + row].Value);
				newItem.SetQuantity(oldSheet.Range["F" + row].Value);
				newItem.SetRate(oldSheet.Range["G" + row].Value);

				string QBPartNum = "";
				string desc = descRange.Value;
				bool isNew = false;

				if (colA is string && colA.Contains("#") && quantRange.Text != "0")
				{
					try
					{
						string QuotePartNum = FindPN(descRange.Text);

						QBPartNum = AllItemList.FindPart(QuotePartNum, ref itemList);

						if (QBPartNum == "")
						{
							QBPartNum = DieSetItem.GetPartNum(QuotePartNum); // if item is die set item, should be in quickbooks as a variable item

						}
						if (QBPartNum == "")
						{
							isNew = true;
							desc = descRange.Value;
							//QBPartNum = genNum.Generate();
						}

						int quant = (int)quantRange.Value;
						double price = priceRange.Value;

						AddItem(QBPartNum, desc, quant, price, isNew);
					}
					catch (NotPartException ex) { }

				}

				colA = oldSheet.Cells[++row, 1].Text;
			}

			AddButtons();
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
			closeButton = new Button();

			closeButton.Text = "Close";
			closeButton.Click += (sender, e) =>
			{
				Globals.ThisAddIn.Application.DisplayAlerts = false;
				sendSheet.Delete();
				Globals.ThisAddIn.Application.DisplayAlerts = true;
			};

			Worksheet sheet = Globals.Factory.GetVstoObject(sendSheet);

			Excel.Range range = sheet.Range["A" + nextRow];
			sheet.Controls.AddControl(closeButton, range, "closeButton");
		}

		internal void AddSendButton()
		{
			sendButton = new Button();

			sendButton.Text = "Send";
			sendButton.Click += (sender, e) =>
			{
				Send();
			};

			Worksheet sheet = Globals.Factory.GetVstoObject(sendSheet);

			Excel.Range range = sheet.Range["B" + nextRow];
			sheet.Controls.AddControl(sendButton, range, "sendButton");
		}

		private void Send()
		{
			AddItemsToQB();
		}

		private void AddItemsToQB()
		{
			List<(string, string)> newItems = new List<(string, string)>();
			for (int i = firstRow; i < nextRow; ++i)
			{
				if (sendSheet.Range["F" + i].Value == "Y")
				{
					string number = sendSheet.Range["A" + i].Value;
					string desc = sendSheet.Range["B" + i].Value;
					newItems.Add((number, desc));
				}
			}
			List<(string, string)> addedList = new List<(string, string)>();
			if (newItems.Count > 0)
			{
				addedList = SendRequest.AddItems(newItems);
			}

			ChangeToAdded(addedList);
		}

		// iterate through items in sendSheet, change isNew to F if in the "addedItems" list
		private void ChangeToAdded(List<(string, string)> addedItems)
		{
			var addedNums = addedItems.Select(x=>x.Item1).ToList();
			for (int i = firstRow;i < nextRow; ++i)
			{
				if (addedNums.Contains(sendSheet.Range["A"+i.ToString()].Value as string))
				{
					sendSheet.Range["F"+i.ToString()].Value = "N";
				}
			}

			foreach ((string num, string desc) in addedItems)
			{
				string[] newItem = new string[2];
				newItem[0] = num;
				newItem[1] = desc;
				this._allItemList.Add(newItem);
			}
		}

		private static class SendRequest
		{
			public static List<(string, string)> AddItems(List<(string, string)> items)
			{
				// Convert format from list to NonInvItem List
				List<NonInvItem> list = new List<NonInvItem>();
				foreach ((string num, string desc) in items)
				{
					NonInvItem item = new NonInvItem();
					item.Name = num;
					item.Desc = desc;
					item.AccountName = "Sales Income";
					list.Add(item);
				}

				AddItemNonInventoryRequest rq = new AddItemNonInventoryRequest(list);
				rq.Connect();
				List<StatusResponse> rs = rq.Send();
				rq.Disconnect();

				// if item was succesfully added, return it in the list. (So it can be changed from "isNew" = Y)
				List<(string, string)> addedList = new List<(string, string)>();
				int item_idx = 0;
				foreach (StatusResponse status in rs)
				{
					if (status._code == 0)
					{
						addedList.Add((list[item_idx].Name, list[item_idx].Desc));
					}

					item_idx++;
				}

				return addedList;
				
			}

			//private static Dictionary<string, bool> CustomNumberCheck(List<string> allitemList, List<string> customList)
			//{
			//	foreach (string item in customList)
			//	{

			//	}
			//}
		}

		private class SendSheetQuoteItem : IQuoteItem
		{
			Range _number;
			int _numberColumn = 1;
			Range _override;
			int _overrideColumn = 2;
			Range _description;
			int _descriptionColumn = 3;
			Range _rate;
			int _rateColumn = 4;
			Range _quantity;
			int _quantityColumn = 5;
			Range _isNew;
			int _isNewColumn = 6;

			// Gives both Excel Interface and Data
			public SendSheetQuoteItem(Excel.Range lineRange, IQuoteItem quoteItem)
			{
				SetRanges(lineRange);

				SetNumber(quoteItem.GetNumber());
				SetDescription(quoteItem.GetDescription());
				SetRate(quoteItem.GetRate());
				SetQuantity(quoteItem.GetQuantity());
			}

			// Gives it Excel Interface
			public SendSheetQuoteItem(Excel.Range lineRange)
			{
				SetRanges(lineRange);
			}

			// Sets Excel Interface
			private void SetRanges(Excel.Range lineRange)
			{
				_number = lineRange[lineRange.Row, _numberColumn];
				_description = lineRange[lineRange.Row, _descriptionColumn];
				_rate = lineRange[lineRange.Row, _rateColumn];
				_quantity = lineRange[lineRange.Row, _quantityColumn];
			}
			public string GetNumber() { return _number.Value; }
			public void SetNumber(string value) { _number.Value = value; }
			public string GetDescription() { return _description.Value; }
			public void SetDescription(string value) { _description.Value = value; }
			public string GetRate() { return _rate.Value; }
			public void SetRate(string value) { _rate.Value = value; }
			public string GetQuantity() { return _quantity.Value; }
			public void SetQuantity(string value) { _quantity.Value = value; }
			public string GetOverride() { return _override.Value; }
			public void SetOverride(string value ) { _override.Value = value; }
			public string GetIsNew() { return _isNew.Value; }
			public void SetIsNew(string value) { _isNew.Value = value; }
		}
	}

	internal class NumberGenerator
	{
		readonly SortedSet<int> sortedList = new SortedSet<int>();

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

	public class DieSetItem
	{
		DieSetItemType type;
		string QBNum;


		DieSetItem(string partNum)
		{
			if (partNum.StartsWith("BB/"))
			{
				type = DieSetItemType.BB;
				QBNum = "1-4501";
			}

			else if (partNum.StartsWith("CI/"))
			{
				type = DieSetItemType.CI;
				QBNum = "1-4502";
			}

			else if (partNum.StartsWith("CD")) // CD or CDX
			{
				type = DieSetItemType.CD;
				QBNum = "1-4503";
			}

			else if (partNum.StartsWith("PD")) // PD or PDX
			{
				type = DieSetItemType.PD;
				QBNum = "1-4504";
			}

			else
			{
				type = DieSetItemType.NONE;
				QBNum = "";
			}
		}

		public static string GetPartNum(string partNumString)
		{
			DieSetItem item = new DieSetItem(partNumString);
			return item.QBNum;
		}
	}

	public enum DieSetItemType
	{
		BB,
		CI,
		CD,
		PD,
		NONE
	}

}
