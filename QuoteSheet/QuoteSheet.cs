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
		public Excel.Worksheet sheet { get; protected set; }
		public int lastRow { get; protected set; }
		public class NotQuoteException : Exception
		{
			public NotQuoteException(string message = "Sheet is not in quote format") : base(message) { }
		}
	}
	internal class StandardQuoteSheet : QuoteSheet
	{
		static private int ITEMS_START_ROW = 22;
		static private int SPACES_FOR_DESCRIPTION = 5;
		static string QUOTE_TITLE = "BEND TOOLING INC.";

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
}
