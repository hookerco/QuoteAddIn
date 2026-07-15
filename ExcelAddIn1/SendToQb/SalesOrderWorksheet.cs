using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Excel = Microsoft.Office.Interop.Excel;
using Button = Microsoft.Office.Tools.Excel.Controls.Button;
using Worksheet = Microsoft.Office.Tools.Excel.Worksheet;
using Microsoft.Office.Tools.Excel;
using System.Windows.Forms;
using QuickBooksIPCContracts;
using ExcelAddIn1.SendToQb;

namespace ExcelAddIn1
{
	// Represents the little sheet that pops up after pressing "Prepare for Sales Order"
	internal class SalesOrderWorksheet
	{
		private readonly int firstRow = 5; // first row of items
		private int nextRow = 5; // next unused row
		private readonly Excel.Worksheet oldSheet; // sheet that we're reading from
		private readonly string quoteReference;
		private readonly string quoteFamily;
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
			Excel.Workbook sourceBook = oldSheet.Parent as Excel.Workbook;
			quoteReference = ExcelAddIn1.Audit.QuoteAuditLog.ReadQuoteReference(sourceBook);
			quoteFamily = ExcelAddIn1.Audit.QuoteAuditLog.QuoteFamily(oldSheet);
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
					// make new baseQuote to pass into SOSheetQuoteItem
					BaseQuoteItem newQuote = new BaseQuoteItem();
					newQuote.SetNumber("");
					newQuote.SetDescription(oldSheet.Range["B" + row].Value);
					newQuote.SetQuantity((int)oldSheet.Range["F" + row].Value);
					newQuote.SetRate((double)oldSheet.Range["G" + row].Value);

					SOSheetQuoteItem newItem = new SOSheetQuoteItem(newQuote, nextRow);

					AddItem(newItem);
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

		internal void AddButtons()
		{
			AddCloseButton();
			AddSendButton();
            AddSwitch_SO_EstimateButton();
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

		internal void AddSwitch_SO_EstimateButton()
        {
			ComboBox typeBox = new ComboBox();
            typeBox.Items.Add("Sales Order");
            typeBox.Items.Add("Estimate");
            typeBox.SelectedIndex = 0;

            Worksheet sheet = Globals.Factory.GetVstoObject(soSheet);

			Excel.Range range = soSheet.Range["C" + nextRow];
			sheet.Controls.AddControl(typeBox, range, "typeBox");
        }

        private void Send()
		{
			List<SOSheetQuoteItem> salesOrderList = null;
			string customer = "", po = "", type_of_txn = "";
			DateTime dueDate = DateTime.Now;
			try
			{
				soSheet.Unprotect();
				customer = soSheet.Cells[1, 1].Text;
				po = soSheet.Cells[2, 1].Text;
				dueDate = DateTime.Parse(soSheet.Cells[3, 1].Text);

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

				salesOrderList = GetItemsOnSheet();

				QBStatusResponse<List<QBItem>> catalogResponse = new QBConnector().Client.GetAllItems();
				if (catalogResponse.StatusCode != 0 || catalogResponse.Data == null)
				{
					MessageBox.Show("Error retrieving the item list from QuickBooks. The order was not sent.");
					return;
				}

				List<QBItem> itemsToCreate = SalesOrderItemResolution.ResolveNumbers(salesOrderList, catalogResponse.Data);
				if (itemsToCreate.Count > 0)
				{
					SendRequest.AddItems(itemsToCreate);
				}

				MarkNumbersOnSheet(salesOrderList);

				type_of_txn = GetComboBoxSelection();

				QBStatusResponse<string> response =
					SendRequest.SendOrder(salesOrderList, customer, po, dueDate, type_of_txn, quoteReference);

				RecordSend(salesOrderList, customer, po, dueDate, type_of_txn, response, "");

				if (response.StatusCode != 0)
				{
					MessageBox.Show("Error sending sales order to QuickBooks");
					return;
				}

				sent = true;

				soSheet.Cells.Locked = true;
				soSheet.Protect();
			}
			catch (Exception ex)
			{
				// AUDIT: capture the errored attempt too (best-effort) so a QB
				// failure still records the source for replay.
				RecordSend(salesOrderList, customer, po, dueDate, type_of_txn, null, ex.Message);
				MessageBox.Show(ex.Message + "\n\n" + ex.StackTrace);
			}
		}

		// AUDIT: build + drop the send record. Best-effort; never throws.
		private void RecordSend(
			List<SOSheetQuoteItem> salesOrderList, string customer, string po,
			DateTime dueDate, string type_of_txn,
			QBStatusResponse<string> response, string errorMessage)
		{
			try
			{
				Excel.Workbook auxBook = oldSheet.Parent as Excel.Workbook;
				var sources = ExcelAddIn1.Audit.QuoteAuditLog.ReadProvenance(auxBook);
				if (ExcelAddIn1.Audit.QuoteAuditLog.IsFullRoundWorkbook(auxBook))
				{
					var direct = ExcelAddIn1.Audit.QuoteAuditLog.SnapshotWorkbook(auxBook, "send_active");
					if (direct != null && !sources.Exists(s => s.Sha256 == direct.Sha256))
						sources.Add(direct);
				}
				else if (sources.Count == 0)
				{
					// AUDIT: aux books created before the audit feature carry no
					// provenance sheet; snapshot the aux book itself so the send
					// still captures what was actually sent.
					var fallback = ExcelAddIn1.Audit.QuoteAuditLog.SnapshotWorkbook(auxBook, "send_aux");
					if (fallback != null) sources.Add(fallback);
				}
				var sentLines = new List<Dictionary<string, object>>();
				if (salesOrderList != null)
					foreach (var it in salesOrderList)
						sentLines.Add(new Dictionary<string, object> {
							{ "number", it.GetInputNumber() }, { "description", it.GetDescription() },
							{ "quantity", it.GetQuantity() }, { "rate", it.GetRate() },
							{ "override_number", it.GetOverride() ?? "" }
						});
				ExcelAddIn1.Audit.QuoteAuditLog.WriteSendRecord(
					auxBook, sources, sentLines, customer, po,
					dueDate.ToString("yyyy-MM-dd"), type_of_txn, quoteReference, quoteFamily,
					response, errorMessage);
			}
			catch { }
		}

        // Assuming 'selectionBox' is the name of your ComboBox control on the sheet
        private string GetComboBoxSelection()
        {
            // Get the VSTO version of the worksheet
            Worksheet vstoSheet = Globals.Factory.GetVstoObject(soSheet);

            // Retrieve the ComboBox control from the worksheet
            var control = vstoSheet.Controls["typeBox"];

            ComboBox comboBox = control as ComboBox;

            // Check if the ComboBox exists and has a selection
            if (control != null && comboBox.SelectedItem != null)
            {
                return comboBox.SelectedItem.ToString(); // Return the selected value
            }

			throw new Exception("Please select a transaction type");
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

		private void MarkNumbersOnSheet(List<SOSheetQuoteItem> itemList)
		{
			for (int i = 0; i < itemList.Count; ++i)
			{
				soSheet.Cells[itemList[i].Row, _numberColumn].Value = itemList[i].GetNumber();
			}
		}

		private static class SendRequest
		{
			internal static void AddItems(List<QBItem> items)
			{
				QBConnector qBConnector = new QBConnector();
				var response = qBConnector.Client.AddNonInvItem(items);

				foreach (var status in response)
				{
					if (status.StatusCode != 0)
					{
						throw new Exception("Error adding items to QuickBooks");
					}
				}
			}

			internal static QBStatusResponse<string> SendOrder(List<SOSheetQuoteItem> items, string customer, string po, DateTime dueDate, string type, string quoteReference)
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
                    QuoteNumber = quoteReference,
                    Customer = cust,
                    DueDate = dueDate,
                    Items = qbItems
                };

                QBConnector qBConnector = new QBConnector();


				QBStatusResponse<string> response = type == "Sales Order" ? qBConnector.Client.AddOrder(order) : qBConnector.Client.AddEstimate(order);

				return response;
			}
		}

	}

}
