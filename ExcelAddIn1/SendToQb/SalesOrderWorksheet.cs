using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Excel = Microsoft.Office.Interop.Excel;
using Button = Microsoft.Office.Tools.Excel.Controls.Button;
using Worksheet = Microsoft.Office.Tools.Excel.Worksheet;
using QBRequestLibrary;
using System.Diagnostics;

namespace ExcelAddIn1
{
	public class NotPartException : Exception
	{
		public NotPartException() { }
	}

	// Represents the little sheet that pops up after pressing "Prepare for Sales Order"
	internal class SalesOrderWorksheet
	{
		private int firstRow; // first row of items
		private int nextRow; // next unused row
		private readonly Excel.Worksheet oldSheet; // sheet that we're reading from
		private readonly Excel.Worksheet soSheet; // underlying Excel sheet
		private Button closeButton; 
		private Button sendButton;

		private static int _numberColumn = 1;
		private static int _overrideColumn = 2;
		private static int _descriptionColumn = 3;
		private static int _quantityColumn = 4;
		private static int _rateColumn = 5;

		internal SalesOrderWorksheet(string customer, Excel.Worksheet oldSheet)
		{
			soSheet = oldSheet.Application.Worksheets.Add() as Excel.Worksheet;

			soSheet.Name = "Send Quote";
			soSheet.Cells[1, 1] = customer;
			soSheet.Cells[2, _numberColumn] = "Item";
			soSheet.Cells[2, _descriptionColumn] = "Description";
			soSheet.Cells[2, _quantityColumn] = "Quantity";
			soSheet.Cells[2, _rateColumn] = "Rate";
			soSheet.Cells[2, _overrideColumn] = "# Override";

			nextRow = 3;
			firstRow = 3;
			this.oldSheet = oldSheet;
		}

		/// <summary>
		/// Add an item to a row of the sheet.
		/// </summary>
		/// <param name="item">The SOSheetQuoteItem to add.</param>
		private void AddItem(SOSheetQuoteItem item)
		{
			soSheet.Cells[nextRow, _numberColumn].Value = item.GetNumber();

			soSheet.Cells[nextRow, _descriptionColumn].Value = item.GetDescription();

			soSheet.Cells[nextRow, _quantityColumn].Value = item.GetQuantity();

			soSheet.Cells[nextRow, _rateColumn].Value = item.GetRate();

			nextRow++;
		}


		private bool IsValidItem(Excel.Worksheet sheet, int row)
		{

			// like #1 or #234
			Regex re = new Regex(@"^#\d*");
			string number = oldSheet.Range[$"A{row}"].Text;
			string quantity = oldSheet.Range[$"F{row}"].Text;


			if (!(number is string)) { return false; }
			if (!re.IsMatch(number)) { return false; }
			if ( quantity == "0") { return false; }

			object quant = oldSheet.Cells[row, 6].Value;
			if (quant.GetType() != typeof(double)) { return false; }
			object rate = oldSheet.Cells[row, 7].Value;
			if (rate.GetType() != typeof(double)) { return false; }

			return true;
		}
		// turns old sheet into SalesOrderSheet (needs the allItemsList from QB to see which part nos. are used)
		internal void ConvertSheet()
		{
			soSheet.Columns[1].NumberFormat = "@";
			int row = 22;
			string rowNumber = oldSheet.Cells[row, 1].Text;

			while (!rowNumber.StartsWith("Total"))
			{
				if (IsValidItem(oldSheet, row))
				{
					try
					{
						// make new baseQuote to pass into SOSheetQuoteItem
						BaseQuoteItem newQuote = new BaseQuoteItem();
						newQuote.SetNumber("");
						newQuote.SetDescription(oldSheet.Range["B" + row].Value);
						newQuote.SetQuantity((int)oldSheet.Range["F" + row].Value);
						newQuote.SetRate((double)oldSheet.Range["G" + row].Value);

						SOSheetQuoteItem newItem = new SOSheetQuoteItem(newQuote, nextRow);

						AddItem(newItem);
					}
					catch (NotPartException) { }

				}

				rowNumber = oldSheet.Cells[++row, 1].Text;
			}

			AddButtons();
		}

		// Input is string, will find part number. Part number is the text before first comma.
		internal static string FindPNinDescription(string desc)
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
			soSheet.Cells[nextRow++, 1].Value = "End Items";
			AddCloseButton();
			AddSendButton();
			nextRow--; // decrement so that nextRow goes back to the "End Items" line
		}

		internal void AddCloseButton()
		{
			closeButton = new Button();

			closeButton.Text = "Close";
			closeButton.Click += (sender, e) =>
			{
				Globals.ThisAddIn.Application.DisplayAlerts = false;
				soSheet.Delete();
				Globals.ThisAddIn.Application.DisplayAlerts = true;
			};

			Worksheet sheet = Globals.Factory.GetVstoObject(soSheet);

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

			Worksheet sheet = Globals.Factory.GetVstoObject(soSheet);

			Excel.Range range = sheet.Range["B" + nextRow];
			sheet.Controls.AddControl(sendButton, range, "sendButton");
		}

		private void Send()
		{
			List<SOSheetQuoteItem> salesOrderList = ListOfItems();

			List<string[]> allItemList = new List<string[]>();
			AllItemList.QueryItems(allItemList);

			AddUnknownItemsToQB(salesOrderList, allItemList);

			string message = "";
			foreach (SOSheetQuoteItem item in salesOrderList)
			{
				message += $"{item.GetNumber()}: {item.GetDescription()}\n\n";
			}
			System.Windows.Forms.MessageBox.Show(message);
		}

		
		private List<SOSheetQuoteItem> ListOfItems()
		{ 
			List<SOSheetQuoteItem> itemList = new List<SOSheetQuoteItem>();

			for (int i = firstRow; i < nextRow; i++)
			{
				string overrideNum = soSheet.Cells[i, _overrideColumn].Value;
				string description = soSheet.Cells[i, _descriptionColumn].Value;
				int quantity = (int)soSheet.Cells[i, _quantityColumn].Value; // cast to int bc excel only uses doubles
				double rate = soSheet.Cells[i, _rateColumn].Value;

				BaseQuoteItem baseItem = new BaseQuoteItem();
				baseItem.SetDescription(description);
				baseItem.SetQuantity(quantity);
				baseItem.SetRate(rate);

				SOSheetQuoteItem newItem = new SOSheetQuoteItem(baseItem, i);
				newItem.SetOverride(overrideNum);

				itemList.Add(newItem);
			}

			return itemList;
		}

		/// <summary>
		/// Takes all items in salesOrderItems that do not appear in allItemList and adds them to QuickBooks
		/// </summary>
		/// <param name="salesOrderItems"></param>
		/// <param name="allItemList"></param>
		private void AddUnknownItemsToQB(List<SOSheetQuoteItem> salesOrderItems, List<string[]> allItemList)
		{
			List<(string, string)> newItems = new List<(string, string)>();
			NumberGenerator generator = new NumberGenerator(allItemList);

			for (int i = 0; i < salesOrderItems.Count; ++i)
			{
				SOSheetQuoteItem item = salesOrderItems[i];
				string overrideNum = item.GetOverride();
				string desc = item.GetDescription();

				// if overridden, check if it's in allItemList
				// if it isn't, add it to newItems
				if (overrideNum != "" && overrideNum != null)
				{
					if (!CheckOverride(ref item, ref allItemList))
					{
						newItems.Add((item.GetNumber(), item.GetDescription()));
					}
				}
				
				// if not overridden, find the correct item number 
				else
				{
					string QuotePartNum = FindPNinDescription(item.GetDescription());

					if (CheckIfExists(ref item, QuotePartNum, ref allItemList)) {
						continue;
					}

					GetCorrectItemNumber(ref item, QuotePartNum, generator);
					newItems.Add((item.GetNumber(), item.GetDescription()));
				}
			}

			if (newItems.Count > 0)
			{
				//addedList = SendRequest.AddItems(newItems);
			}

			ChangeToAdded(newItems, allItemList);
		}

		/// <summary>
		/// Returns true if override is in AllItemsList. Also modifies item so that item.number = item.override
		/// </summary>
		/// <param name="item">SOSheetQuoteItem of item to check</param>
		/// <param name="allItemList"></param>
		/// <returns>a bool that indicates whether the item's override number is in allItemsList</returns>
		private bool CheckOverride(ref SOSheetQuoteItem item, ref List<string[]> allItemList)
		{
			string number = AllItemList.FindSerialNumber(item.GetOverride(), ref allItemList);
			item.SetNumber(item.GetOverride());
			return number == "" ? false : true;
		}



		/// <summary>
		/// Checks if the item exists in the allItemList and sets the item number accordingly.
		/// </summary>
		/// <param name="item">The SOSheetQuoteItem to check</param>
		/// <param name="allItemList">The list of all items</param>
		private bool CheckIfExists(ref SOSheetQuoteItem item, string QuotePartNum, ref List<string[]> allItemList)
		{
			item.SetNumber(AllItemList.FindMPN(QuotePartNum, ref allItemList));

			if (item.GetNumber() != "") // if new item isn't in allItemList from QB
			{
				return true; // if item is die set item, should be in quickbooks as a variable item
			}

			return false;
		}

		private void GetCorrectItemNumber(ref SOSheetQuoteItem item, string QuotePartNum, NumberGenerator numberGenerator)
		{


			if (item.GetNumber() == "") // if new item isn't in allItemList from QB
			{
				item.SetNumber(DieSetItem.GetPartNum(QuotePartNum)); // if item is die set item, should be in quickbooks as a variable item
			}

			if (item.GetNumber() == "")
			{
				item.SetNumber(DieSetItem.GetPartNum(QuotePartNum));
			}

			if (item.GetNumber() == "")
			{
				item.SetNumber(numberGenerator.Generate());
			}

		}

		// iterate through items in soSheet, change isNew to F if in the "addedItems" salesOrderList
		private void ChangeToAdded(List<(string, string)> addedItems, List<string[]> allItemList)
		{
			foreach ((string num, string desc) in addedItems)
			{
				string[] newItem = new string[2];
				newItem[0] = num;
				newItem[1] = desc;
				allItemList.Add(newItem);
			}
		}

		private static class SendRequest
		{
			public static List<(string, string)> AddItems(List<(string, string)> items)
			{
				// Convert format from salesOrderList to NonInvItem List
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

				// if item was succesfully added, return it in the salesOrderList. (So it can be changed from "isNew" = Y)
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
		}

		private class SOSheetQuoteItem : IQuoteItem
		{
			IQuoteItem internalQuote;
			string _override;

			// Gives both Excel Interface and Data
			public SOSheetQuoteItem(IQuoteItem quoteItem, int row)
			{
				internalQuote = quoteItem;
				Row = row;
			}

			public int Row { get; set; }

			public string GetInputNumber() => GetOverride() == "" ? GetNumber() : GetOverride();

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