using Microsoft.Office.Interop.Excel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Excel = Microsoft.Office.Interop.Excel;

namespace ExcelAddIn1
{
	internal partial class QuoteBuilder
	{
		static string QUOTE_TITLE = "BEND TOOLING INC.";

		internal static bool IsQuote(Excel.Worksheet worksheet)
		{
			return worksheet.Range["C1"].Text == QUOTE_TITLE;
		}

		internal static bool IsNotQuote(Excel.Worksheet worksheet)
		{
			return !IsQuote(worksheet);
		}

		internal static int GetLastRow(Excel.Worksheet worksheet)
		{
			int row = ITEMS_START_ROW;
			string colA = worksheet.Cells.Range["A" + row].Text;
			while (!colA.Contains("Total"))
			{
				row++;
				colA = worksheet.Cells.Range["A" + row].Text;
			}
			return row - 1;
		}
	}
}
