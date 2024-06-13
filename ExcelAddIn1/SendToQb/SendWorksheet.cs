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

namespace ExcelAddIn1
{
	public class NotPartException : Exception
	{
		public NotPartException() { }
	}

	internal class SendWorksheet
	{
		private int firstRow;
		private int nextRow;
		private readonly Excel.Worksheet oldSheet;
		private readonly Excel.Worksheet sendSheet;
		private Button closeButton;
		private Button sendButton;
		private List<string[]> _allItemList;
		private List<SendSheetQuoteItem> _currItemList= new List<SendSheetQuoteItem>();

		private static int _numberColumn = 1;
		private static int _overrideColumn = 2;
		private static int _descriptionColumn = 3;
		private static int _quantityColumn = 4;
		private static int _rateColumn = 5;
		private static int _isNewColumn = 6;

		internal SendWorksheet(string customer, Excel.Worksheet oldSheet)
		{
			sendSheet = oldSheet.Application.Worksheets.Add() as Excel.Worksheet;

			sendSheet.Name = "Send Quote";
			sendSheet.Cells[1, 1] = customer;
			sendSheet.Cells[2, _numberColumn] = "Number";
			sendSheet.Cells[2, _descriptionColumn] = "Description";
			sendSheet.Cells[2, _quantityColumn] = "Quantity";
			sendSheet.Cells[2, _rateColumn] = "Rate";
			sendSheet.Cells[2, _overrideColumn] = "# Override";
			sendSheet.Cells[2, _isNewColumn] = "IsNew";

			nextRow = 3;
			firstRow = 3;
			this.oldSheet = oldSheet;
		}

		private void AddItem(SendSheetQuoteItem item)
		{
			sendSheet.Cells[nextRow, 1].Value = item.GetNumber();
			sendSheet.Cells[nextRow, 2].Value = item.GetDescription();
			sendSheet.Cells[nextRow, 3].Value = item.GetQuantity();
			sendSheet.Cells[nextRow, 4].Value = item.GetRate();
			sendSheet.Cells[nextRow, 6].Value = item.GetIsNew() ? "Y" : "N";

			nextRow++;
		}

		internal void ConvertSheet(ref List<string[]> itemList)
		{
			_allItemList = itemList;
			sendSheet.Columns[1].NumberFormat = "@";
			int row = 22;
			string colA = oldSheet.Cells[row, 1].Text;
			NumberGenerator genNum = new NumberGenerator(_allItemList);

			while (!colA.Contains("Total"))
			{
				if (colA is string && colA.Contains("#") && oldSheet.Cells[row, 6].Text != "0")
				{
					try
					{
						// make new baseQuote to pass into SendSheetQuoteItem
						BaseQuote newQuote = new BaseQuote();
						newQuote.SetNumber("");
						newQuote.SetDescription(oldSheet.Range["B" + row].Value);
						newQuote.SetQuantity((int)oldSheet.Range["F" + row].Value);
						newQuote.SetRate((double)oldSheet.Range["G" + row].Value);

						SendSheetQuoteItem newItem = new SendSheetQuoteItem(newQuote, nextRow);
						_currItemList.Add(newItem);
						newItem.SetIsNew(false);

						string QuotePartNum = FindPN(newItem.GetDescription());

						newItem.SetNumber(AllItemList.FindPart(QuotePartNum, ref _allItemList));
						
						if (newItem.GetNumber() == "") // if new item isn't in allItemList from QB
						{
							newItem.SetNumber(DieSetItem.GetPartNum(QuotePartNum)); // if item is die set item, should be in quickbooks as a variable item

						}
						if (newItem.GetNumber() == "") // if new item isn't part of the die set
						{
							newItem.SetIsNew(true); // Then it needs a new serialized part number
							//newItem.SetNumber(genNum.Generate());
						}

						AddItem(newItem);
					}
					catch (NotPartException) { }

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
			sendSheet.Cells[nextRow++, 1].Value = "End Items";
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

		private class SendSheetQuoteItem
		{
			IQuoteItem internalQuote;
			string _override;
			bool _isNew;
			int _row;

			// Gives both Excel Interface and Data
			public SendSheetQuoteItem(IQuoteItem quoteItem, int row)
			{
				internalQuote = quoteItem;
				_row = row;
			}

			// Sets Excel Interface

			public string GetNumber() { return internalQuote.GetNumber(); }
			public void SetNumber(string value) { internalQuote.SetNumber(value); }
			public string GetDescription() { return internalQuote.GetDescription(); }
			public void SetDescription(string value) { internalQuote.SetDescription(value); }
			public double GetRate() { return internalQuote.GetRate(); }
			public void SetRate(double value) { internalQuote.SetRate(value); }
			public int GetQuantity() { return internalQuote.GetQuantity(); }
			public void SetQuantity(int value) { internalQuote.SetQuantity(value); }
			public string GetOverride() { return _override; }
			public void SetOverride(string value ) { _override = value; }
			public bool GetIsNew() { return _isNew; }
			public void SetIsNew(bool value) { _isNew = value; }
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
