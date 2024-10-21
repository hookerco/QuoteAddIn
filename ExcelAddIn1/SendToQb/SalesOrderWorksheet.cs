using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Excel = Microsoft.Office.Interop.Excel;
using Button = Microsoft.Office.Tools.Excel.Controls.Button;
using Worksheet = Microsoft.Office.Tools.Excel.Worksheet;
using System.Windows.Forms;
using QuickBooksIPCContracts;
using ExcelAddIn1.SendToQb;

namespace ExcelAddIn1
{
	public class NotPartException : Exception
	{
		public NotPartException() { }
	}

	// Represents the little sheet that pops up after pressing "Prepare for Sales Order"
	internal class SalesOrderWorksheet
	{
		private readonly int firstRow = 5; // first row of items
		private int nextRow = 5; // next unused row
		private readonly Excel.Worksheet oldSheet; // sheet that we're reading from
		private readonly Excel.Worksheet soSheet; // underlying Excel sheet
		private Button closeButton; 
		private Button sendButton;
		private bool sent = false;
		private readonly static int _numberColumn = 1;
		private readonly static int _overrideColumn = 2;
		private readonly static int _descriptionColumn = 3;
		private readonly static int _quantityColumn = 4;
		private readonly static int _rateColumn = 5;
		private readonly static int _customerRow = 1;
		private readonly static int _PORow = 2;
		private readonly static int _dueDateRow = 3;
		private readonly static int _labelRow = 4;

		internal SalesOrderWorksheet(string customer, Excel.Worksheet oldSheet)
		{
			soSheet = oldSheet.Application.Worksheets.Add() as Excel.Worksheet;

			// To ensure I can lock cells when needed
			soSheet.Cells.Locked = false;

			soSheet.Name = "Send Quote";
			soSheet.Cells[_customerRow, 1] = customer;
			soSheet.Cells[_customerRow, 1].Locked = true;
			soSheet.Cells[_PORow, 1] = "Type customer PO# here";
			soSheet.Cells[_PORow, 1].Locked = false;
			soSheet.Cells[_dueDateRow, 1] = "Due Date";
			soSheet.Cells[_dueDateRow, 1].Locked = false;

			soSheet.Cells[_labelRow, _numberColumn] = "Number";
			soSheet.Cells[_labelRow, _descriptionColumn] = "Description";
			soSheet.Cells[_labelRow, _quantityColumn] = "Quantity";
			soSheet.Cells[_labelRow, _rateColumn] = "Rate";
			soSheet.Cells[_labelRow, _overrideColumn] = "# Override";


			this.oldSheet = oldSheet;
		}

		/// <summary>
		/// Add an item to the next row of the SalesOrderSheet. Modifies <c>nextRow</c>
		/// </summary>
		/// <param name="item">The SOSheetQuoteItem to add.</param>
		private void AddItem(SOSheetQuoteItem item)
		{
			Excel.Range numRange = soSheet.Cells[nextRow, _numberColumn];
			numRange.Locked = true;

			soSheet.Cells[nextRow, _descriptionColumn].Value = item.GetDescription();

			soSheet.Cells[nextRow, _quantityColumn].Value = item.GetQuantity();

			soSheet.Cells[nextRow, _rateColumn].Value = item.GetRate();

			nextRow++;
		}


		private bool IsValidItem(int row)
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
			int row = 15;
			string rowNumber = oldSheet.Cells[row, 1].Text;

			int weeks = FindLeadTimeWeeks();
			string dueDate = CalculateDueDate.calculateDueDate(weeks);
			soSheet.Cells[_dueDateRow, 1] = dueDate;
			soSheet.Cells[_dueDateRow, 1].Locked = true;

			while (!rowNumber.StartsWith("Total"))
			{
				if (IsValidItem(row))
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

			soSheet.Protect();
		}

		private int FindLeadTimeWeeks()
		{
			int row = nextRow;
			string title = oldSheet.Cells[row, 1].Text;
			while (!title.StartsWith("Lead time:"))
			{
				++row;
				title = oldSheet.Cells[row, 1].Text;
				if (row > 500)
				{
					throw new Exception("Lead Time not found"); // no unlimited loops
				}
			}

			Regex regex = new Regex(@"^Lead time: +[0-9]+-(?<LeadTimeWeeks>[0-9]+).*$");
			string leadTime = oldSheet.Cells[row, 1].Text;
			Match match = regex.Match(leadTime);
			if (!match.Success)
			{
				throw new Exception("Lead Time not found");
			}
			int weeks = int.Parse(match.Groups["LeadTimeWeeks"].Value);

			return weeks;
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
			AddCloseButton();
			AddSendButton();
		}

		internal void AddCloseButton()
		{
			closeButton = new Button
			{
				Text = "Close"
			};
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
			sendButton = new Button
			{
				Text = "Send"
			};
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
			try
			{
				soSheet.Unprotect();
				string customer = soSheet.Cells[1, 1].Text;
				string po = soSheet.Cells[2, 1].Text;
				DateTime dueDate = DateTime.Parse(soSheet.Cells[3, 1].Text);

				if (customer == "" || customer == "Customer not found")
				{
					MessageBox.Show("Please enter customer name from QuickBooks in cell A1");
					return;
				}

				if (sent)
				{
					MessageBox.Show("SalesOrder already sent. Please close and reopen the sheet to send again.");
					return;
				}

				List<SOSheetQuoteItem> salesOrderList = GetItemsOnSheet();

				AllItemList allItemList = new AllItemList();
				allItemList.QueryItems();

				AddUnknownItemsToQB(salesOrderList, allItemList);

				MarkNumbersOnSheet(salesOrderList);

				SendRequest.SendSalesOrder(salesOrderList, customer, po, dueDate);

				sent = true;

				soSheet.Cells.Locked = true;
				soSheet.Protect();
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message + "\n\n" + ex.StackTrace);
			}
		}

		
		private List<SOSheetQuoteItem> GetItemsOnSheet()
		{ 
			List<SOSheetQuoteItem> itemList = new List<SOSheetQuoteItem>();

			for (int i = firstRow; i < nextRow; i++)
			{
				string overrideNum = soSheet.Cells[i, _overrideColumn].Text;
				string description = soSheet.Cells[i, _descriptionColumn].Text;
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
		private void AddUnknownItemsToQB(List<SOSheetQuoteItem> salesOrderItems, AllItemList allItemList)
		{
			List<(string, string)> newItems = new List<(string, string)>();
			NumberGenerator generator = new NumberGenerator(allItemList);

			for (int i = 0; i < salesOrderItems.Count; ++i)
			{
				SOSheetQuoteItem item = salesOrderItems[i];
				string overrideNum = item.GetOverride();

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

					if (CheckIfExists(ref item, QuotePartNum, ref allItemList)) 
					{
						continue;
					}

					bool isDieSet = GetCorrectItemNumber(ref item, QuotePartNum, generator);

					if (!isDieSet)
					{
						newItems.Add((item.GetNumber(), item.GetDescription()));
						allItemList.Add(item);
					}
				}
			}

			if (newItems.Count > 0)
			{
				SendRequest.AddItems(newItems);
			}
		}

		/// <summary>
		/// Returns true if override is in AllItemsList. Also modifies item so that item.number = item.override
		/// </summary>
		/// <param name="item">SOSheetQuoteItem of item to check</param>
		/// <param name="allItemList"></param>
		/// <returns>a bool that indicates whether the item's override number is in allItemsList</returns>
		private bool CheckOverride(ref SOSheetQuoteItem item, ref AllItemList allItemList)
		{
			string number = allItemList.FindSerialNumber(item.GetOverride());
			item.SetNumber(item.GetOverride());
			return number != "";
		}



		/// <summary>
		/// Checks if the item exists in the allItemList and sets the item number accordingly.
		/// </summary>
		/// <param name="item">The SOSheetQuoteItem to check</param>
		/// <param name="allItemList">The list of all items</param>
		private bool CheckIfExists(ref SOSheetQuoteItem item, string QuotePartNum, ref AllItemList allItemList)
		{
			item.SetNumber(allItemList.FindMPN(QuotePartNum));

			if (item.GetNumber() != "") // if new item isn't in allItemList from QB
			{
				return true; // if item is die set item, should be in quickbooks as a variable item
			}

			return false;
		}

		/// <summary>
		/// Gives the items the correct item number. Returns true if DieSetItem, false if generated number.
		/// </summary>
		/// <param name="item"></param>
		/// <param name="QuotePartNum"></param>
		/// <param name="numberGenerator"></param>
		/// <returns></returns>
		private bool GetCorrectItemNumber(ref SOSheetQuoteItem item, string QuotePartNum, NumberGenerator numberGenerator)
		{


			if (item.GetNumber() == "") // if new item isn't in allItemList from QB
			{
				item.SetNumber(DieSetItem.GetPartNum(QuotePartNum)); // if item is die set item, should be in quickbooks as a variable item

				if (item.GetNumber() != "")
				{
					return true;
				}
			}

			if (item.GetNumber() == "")
			{
				item.SetNumber(numberGenerator.Generate());
			}

			return false;
		}

		// iterate through items in soSheet, change isNew to F if in the "addedItems" salesOrderList
		private void ChangeToAdded(string num, string desc, AllItemList allItemList)
		{
			BaseQuoteItem newItem = new BaseQuoteItem();
			newItem.SetNumber(num);
			newItem.SetDescription(desc);
			allItemList.Add(newItem);
			
		}

		private void MarkNumbersOnSheet(List<SOSheetQuoteItem> itemList)
		{
			for (int i = 0; i < itemList.Count; ++i)
			{
				soSheet.Cells[itemList[i].Row, _numberColumn].Value = itemList[i].GetNumber();
			}
		}

		private static class SendRequest
		{
			internal static void AddItems(List<(string, string)> items)
			{
				// Convert format from salesOrderList to NonInvItem List
				List<QBItem> list = new List<QBItem>();
				foreach ((string num, string desc) in items)
				{
                    QBItem item = new QBItem
                    {
						Number = num,
						Description = desc,
						AccountName = "Sales Income"
					};
					list.Add(item);
				}

				QBConnector qBConnector = new QBConnector();
				var response = qBConnector.Client.AddNonInvItem(list);

				foreach (var status in response)
				{
					if (status.StatusCode != 0)
					{
						throw new Exception("Error adding items to QuickBooks");
					}
				}
			}

			internal static void SendSalesOrder(List<SOSheetQuoteItem> items, string customer, string po, DateTime dueDate)
			{
                List<QBItem> qbItems = new List<QBItem>();
				foreach (SOSheetQuoteItem item in items)
				{
					QBItem qbItem = new QBItem
					{
						Number = item.GetNumber(),
						Description = item.GetDescription(),
						Rate = item.GetRate(),
						Quantity = item.GetQuantity()
					};
                    qbItems.Add(qbItem);
                }

                QBCustomer cust = new QBCustomer
                {
                    Name = customer,
                    PO = po
                };

                QBOrder order = new QBOrder
                {
                    Customer = cust,
                    DueDate = dueDate,
                    Items = qbItems
                };

                QBConnector qBConnector = new QBConnector();
                var response = qBConnector.Client.AddOrder(order);

                if (response.StatusCode != 0)
				{
					throw new Exception("Error sending sales order to QuickBooks");
				}
			}
		}

		internal class SOSheetQuoteItem : IQuoteItem
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
		private readonly SortedSet<int> sortedNumberSet = new SortedSet<int>();

		internal NumberGenerator(AllItemList itemList)
		{

			itemList.GetNumberSet(ref sortedNumberSet);
		}

		internal string Generate()
		{
			int count = 1;
			foreach (int partNum in sortedNumberSet)
			{
				if (count != partNum)
				{
					sortedNumberSet.Add(count);
					return "1-" + count.ToString("D4");
				}
				count++;
			}
			sortedNumberSet.Add(sortedNumberSet.Count);

			string num = "1-" + sortedNumberSet.Count.ToString("D4");
			return num;
		}
	}

	public class DieSetItem
	{
		string QBNum;


		DieSetItem(string partNum)
		{
			if (partNum.StartsWith("BB/"))
			{
				QBNum = "1-4501";
			}

			else if (partNum.StartsWith("CI/"))
			{
				QBNum = "1-4502";
			}

			else if (partNum.StartsWith("CD")) // CD or CDX
			{
				QBNum = "1-4503";
			}

			else if (partNum.StartsWith("PD")) // PD or PDX
			{
				QBNum = "1-4504";
			}

			else
			{
				QBNum = "";
			}
		}

		public static string GetPartNum(string partNumString)
		{
			DieSetItem item = new DieSetItem(partNumString);
			return item.QBNum;
		}
	}
}