using System;
using Microsoft.Office.Interop.Excel;
using Excel = Microsoft.Office.Interop.Excel;
using System.Windows.Forms;


namespace ExcelAddIn1
{
	internal static partial class QuoteBuilder
	{
		static private int ITEMS_START_ROW = 22;
		static private int SPACES_FOR_DESCRIPTION = 5;

		/// <summary>Create a new sheet from active sheet</summary>
		/// <remarks>Must be on quote sheet, does not work with price breaks</remarks>
		public static void Create()
		{
			Excel.Worksheet oldSheet = Globals.ThisAddIn.Application.ActiveSheet;
			
			if (IsQuote(oldSheet)) // If the sheet is a quote sheet
			{
				TrytoCopy(oldSheet);

				Excel.Worksheet newSheet = Globals.ThisAddIn.Application.ActiveSheet;

				DeleteButtons(newSheet);

				int row = GetLastRow(newSheet);

				CopyValues(oldSheet, newSheet, row);
				RemoveZerosAndRenumber();
				SaveAs();

			}
			else
			{
				MessageBox.Show("Please make a quote the active sheet");
			}
		}

		internal static void DeleteButtons(Excel.Worksheet worksheet)
		{
			try
			{
				worksheet.Shapes.Item("Button 1").Delete();
				worksheet.Shapes.Item("Button 2").Delete();
				worksheet.Shapes.Item("Button 3").Delete();
			}
			catch (System.ArgumentException) { }
		}

		internal static void TrytoCopy(Excel.Worksheet worksheet)
		{
			try
			{
				worksheet.Copy();
			}
			catch
			{
				MessageBox.Show("Please deselect cell and finalize any changes, then try again");
				return;
			}
		}

		/// <summary>
		/// Adds items from ActiveSheet to previously created quote found at <paramref name="filePath"/>
		/// </summary>
		/// <param name="filePath">Valid filepath of previously created quote</param>
		public static void Add(string filePath)
		{

			Excel.Worksheet oldSheet = Globals.ThisAddIn.Application.ActiveSheet;

			Excel.Workbook newBook;

			try
			{
				newBook = Globals.ThisAddIn.Application.Workbooks.Open(filePath);
				newBook.Activate();
			}
			catch (Exception)
			{
				MessageBox.Show("Please deselect cell and finalize any changes, then try again");
				return;
			}

			// To avoid weird COM error
			Globals.ThisAddIn.Application.WindowState = Excel.XlWindowState.xlNormal;

			Excel.Worksheet newSheet = newBook.Worksheets[1];
			if (IsNotQuote(newSheet))
			{
				newBook.Close();
				MessageBox.Show("File chosen is not a quote");
				return;
			}

			// start of items --- Change if module changes
			int lastRow = GetLastRow(oldSheet);
			lastRow--;

			int numItems = lastRow - ITEMS_START_ROW;

			lastRow = GetLastRow(newSheet);

			AddItems(newSheet, oldSheet, lastRow, numItems);

			AddDesc(newSheet, oldSheet, lastRow);

			AddBackFormulas(newSheet, GetLastRow(newSheet));

			RemoveZerosAndRenumber();
		}

		// Removes zeros from active sheet, and only works with "created" sheet
		private static void RemoveZerosAndRenumber()
		{
			Excel.Worksheet sheet = Globals.ThisAddIn.Application.ActiveSheet;
			int currRow = ITEMS_START_ROW; // Start of items
			string colA = sheet.Range["A" + (1 + currRow)].Text;
			string colF = sheet.Range["F" + currRow].Text;

			int currNum = 1;
			// while the cells aren't empty (may have to change this depending on format)
			while (!colA.Contains("Total"))
			{

				if (colF == "0") // If quantity is 0
				{
					sheet.Rows[currRow].Delete();
				}

				else if (colF != "") // If quantity > 0, Make the new value the current num 
				{
					sheet.Range["A" + currRow].Value = "#" + currNum;
					currNum++;
					currRow++;
				}

				else // if it's not an item
				{
					currRow++;
				}

				colA = sheet.Range["A" + (1+currRow)].Text;
				colF = sheet.Range["F" + currRow].Text;
			}
		}

		// just a SaveAs dialog prompt
		private static void SaveAs()
		{
			SaveFileDialog saveFileDialog = new SaveFileDialog();
			saveFileDialog.Filter = "Excel files (*.xlsx)|*.xlsx|All files (*.*)|*.*";
			saveFileDialog.FilterIndex = 1;
			saveFileDialog.RestoreDirectory = true;

			if (saveFileDialog.ShowDialog() == DialogResult.OK)
			{
				// Save the workbook
				Globals.ThisAddIn.Application.ActiveWorkbook.SaveAs(saveFileDialog.FileName);
			}
		}

		// Add items, used in Add() function
		private static void AddItems(Excel.Worksheet newSheet, Excel.Worksheet oldSheet, int iterator, int numItems)
		{
			for (int i = 0; i < numItems + 1; ++i) // + 1 to add extra space
			{
				newSheet.Rows[iterator].Insert();
			}

			Excel.Range oldRange = oldSheet.Range["A22:H" + (22 + numItems)];
			Excel.Range newRange = newSheet.Cells.Range["A" + iterator + ":H" + (iterator + numItems)];
			oldRange.Copy();
			newRange.PasteSpecial(Paste: XlPasteType.xlPasteValues);

			for (int i = 0; i <= numItems; i++) // Loop is for formatting
			{
				newSheet.Cells.Range["B" + (iterator + i) + ":E" + (iterator + i)].Merge();

				Excel.Range BRange = newSheet.Cells.Range["B" + (iterator + i)];

				// Make new height same as old height
				BRange.Rows.RowHeight = oldSheet.Range["B" + (22 + i)].RowHeight;

				if (BRange.Text == "Mounting Hardware:")
				{
					BRange.Font.Bold = true;
				}
			}
		}

		// Adds order description, used in Add() function
		private static void AddDesc(Excel.Worksheet newSheet, Excel.Worksheet oldSheet, int iterator)
		{
			for (int i = 0; i < SPACES_FOR_DESCRIPTION; i++)
			{
				newSheet.Rows[iterator].Insert();
				newSheet.Rows[iterator].RowHeight = 12.75;
				newSheet.Rows[iterator].VerticalAlignment = Excel.XlVAlign.xlVAlignBottom;

				Excel.Range ARange = newSheet.Range["A" + iterator];
				ARange.HorizontalAlignment = Excel.XlHAlign.xlHAlignLeft;
				Excel.Range BRange = newSheet.Range["B" + iterator];
				BRange.HorizontalAlignment = Excel.XlHAlign.xlHAlignRight;
				Excel.Range DRange = newSheet.Range["D" + iterator];
				DRange.HorizontalAlignment = Excel.XlHAlign.xlHAlignRight;
			}

			oldSheet.Range["A16:E18"].Copy();
			newSheet.Range["A" + (iterator + 1) + ":E" + (iterator + 3)].PasteSpecial(Paste: XlPasteType.xlPasteValues);
		}

		// Locks in values instead of using formulas, used in Create() function
		private static void CopyValues(Excel.Worksheet oldSheet, Excel.Worksheet newSheet, int row)
		{
			newSheet.Range["A1:H" + (row + 13)].Value = oldSheet.Range["A1:H" + (row + 13)].Value;
			AddBackFormulas(newSheet, row);
		}

		private static void AddBackFormulas(Excel.Worksheet newSheet, int row)
		{
			newSheet.Range["H" + (row + 1)].Formula = $"=SUM(H22:H{row})";

			int iterator = ITEMS_START_ROW;
			while (iterator < row)
			{
				try
				{
					if (newSheet.Range["A" + iterator].Value.Contains("#"))
					{
						newSheet.Range["H" + (iterator)].Formula = $"=F{iterator}*G{iterator}";
					}
				}
				catch { }

				iterator++;
			}
		}
	}
}
