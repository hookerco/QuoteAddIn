using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Excel = Microsoft.Office.Interop.Excel;

namespace QuoteSheetLibrary
{
	public abstract class QuoteSheet
	{
		static protected int ITEMS_START_ROW = 22;
		static protected int SPACES_FOR_DESCRIPTION = 5;
		static protected string QUOTE_TITLE = "BEND TOOLING INC.";

		public Excel.Worksheet sheet { get; set; }
		public int lastRow { get; protected set; }
		public class NotQuoteException : Exception
		{
			public NotQuoteException(string message = "Sheet is not in quote format") : base(message) { }
		}

		// Delete Button 2
		// It may not work after copy
		public void DeleteButtons()
		{
			try
			{
				sheet.Shapes.Item("Button 1").Delete();
				sheet.Shapes.Item("Button 2").Delete();
				sheet.Shapes.Item("Button 3").Delete();
			}
			catch (System.ArgumentException) { }
		}
	}
	public class StandardQuoteSheet : QuoteSheet
	{
		public StandardQuoteSheet(Excel.Worksheet sheet)
		{
			if (IsNotQuote(sheet))
			{
				throw new NotQuoteException();
			}

			this.sheet = sheet;
			SetLastRow();
		}

		private void SetLastRow()
		{
			int row = ITEMS_START_ROW;
			string colA = sheet.Cells.Range["A" + row].Text;
			while (!colA.Contains("Total"))
			{
				row++;
				colA = sheet.Cells.Range["A" + row].Text;
			}
			lastRow = row - 1;
		}

		private bool IsQuote(Excel.Worksheet worksheet)
		{
			return worksheet.Range["C1"].Text == QUOTE_TITLE;
		}

		private bool IsNotQuote(Excel.Worksheet worksheet)
		{
			return !IsQuote(worksheet);
		}
	}
}
