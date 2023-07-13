using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Office.Tools.Excel;
using System.Threading.Tasks;
using Microsoft.Office.Interop.Excel;
using Excel = Microsoft.Office.Interop.Excel;
using System.Windows.Forms;
using Microsoft.Office.Tools.Ribbon;
using System.Diagnostics.Eventing.Reader;
using Microsoft.SqlServer.Server;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Interop.QBFC14;

namespace ExcelAddIn1
{
    internal static class QuoteBuilder
    {
        /**
         * <summary>Create a new sheet from active sheet</summary>
         * <remarks>Must be on quote sheet, does not work with price breaks</remarks>
         */
        public static void Create()
        {
            Excel.Worksheet quoteSheet = Globals.ThisAddIn.Application.ActiveSheet;
            
            if (quoteSheet.Range["C1"].Text == "BEND TOOLING INC.") // If the sheet is a quote sheet
            {
                try
                {
                    quoteSheet.Copy();
                }
                catch
                {
                    MessageBox.Show("Please deselect cell and finalize any changes, then try again");
                    return;
                }
                Excel.Worksheet newSheet = Globals.ThisAddIn.Application.ActiveSheet;

                try
                {
                    newSheet.Shapes.Item("Button 1").Delete();
                    newSheet.Shapes.Item("Button 2").Delete();
                    newSheet.Shapes.Item("Button 3").Delete();
                }
                catch { }


                // Creates a stopping point on the quote
                int row = ItemsRow(newSheet);

                CreateCopyVals(quoteSheet, newSheet, row);
                Excel.Range endRange = newSheet.Cells.Range["A" + row];
                endRange.Value = "END";
                endRange.Font.Color = System.Drawing.ColorTranslator.ToOle(System.Drawing.Color.White);


                RemoveZeros();
                SaveAs();

            }
            else
            {
                MessageBox.Show("Please make a quote the active sheet");
            }
        }

        /// <summary>
        /// Adds items from ActiveSheet to previously created quote found at <paramref name="filePath"/>
        /// </summary>
        /// <param name="filePath">Valid filepath of previously created quote</param>
        public static void Add(string filePath)
        {

            Excel.Worksheet oldSheet = Globals.ThisAddIn.Application.ActiveSheet;

            Excel.Workbook masterBook;

            try
            {
                masterBook = Globals.ThisAddIn.Application.Workbooks.Open(filePath);
                masterBook.Activate();
            }
            catch (Exception)
            {
                MessageBox.Show("Please deselect cell and finalize any changes, then try again");
                return;
            }

            // To avoid weird COM error
            Globals.ThisAddIn.Application.WindowState = Excel.XlWindowState.xlNormal;

            Excel.Worksheet masterSheet = masterBook.Worksheets[1];
            if (masterSheet.Cells.Range["C1"].Text != "BEND TOOLING INC.")
            {
                masterBook.Close();
                MessageBox.Show("File chosen is not a quote");
                return;
            }

            // start of items --- Change if module changes
            int iterator = ItemsRow(oldSheet);
            iterator--;

            int numItems = iterator - 22;

            // start of items --- Change if module changes
            iterator = 22;

            // strings of the cells' values
            string colA = masterSheet.Range["A" + iterator].Text;
            // "END" loop because masterSheet now ends in END (see Create())
            while (colA != "END") // Scoping out range of items
            {
                iterator++;
                colA = masterSheet.Range["A" + iterator].Text;
            }

            AddItems(masterSheet, oldSheet, iterator, numItems);

            AddDesc(masterSheet, oldSheet, iterator);

            RemoveZeros();
        }

        // Removes zeros from active sheet, and only works with "created" sheet
        private static void RemoveZeros()
        {
            Excel.Worksheet sheet = Globals.ThisAddIn.Application.ActiveSheet;
            int iterator = 22; // Start of items
            string colA = sheet.Range["A" + iterator].Text;
            string colF = sheet.Range["F" + iterator].Text;

            int currNum = 1;

            // while the cells aren't empty (may have to change this depending on format)
            while (colA != "END")
            {

                if (colF == "0") // If quantity is 0
                {
                    sheet.Rows[iterator].Delete();
                }

                else if (colF != "") // If quantity > 0, Make the new value the current num 
                {
                    sheet.Range["A" + iterator].Value = "#" + currNum;
                    currNum++;
                    iterator++;
                }

                else // if it's not an item
                {
                    iterator++;
                }

                colA = sheet.Range["A" + iterator].Text;
                colF = sheet.Range["F" + iterator].Text;
            }
        }

        // just a SaveAs dialog prompt
        private static void SaveAs()
        {
            SaveFileDialog saveFileDialog1 = new SaveFileDialog();
            saveFileDialog1.Filter = "Excel files (*.xlsx)|*.xlsx|All files (*.*)|*.*";
            saveFileDialog1.FilterIndex = 1;
            saveFileDialog1.RestoreDirectory = true;

            if (saveFileDialog1.ShowDialog() == DialogResult.OK)
            {
                // Save the workbook
                Globals.ThisAddIn.Application.ActiveWorkbook.SaveAs(saveFileDialog1.FileName);
            }
        }

        // Add items, used in Add() function
        private static void AddItems(Excel.Worksheet masterSheet, Excel.Worksheet oldSheet, int iterator, int numItems)
        {
            for (int i = 0; i < numItems + 1; ++i) // + 1 to add extra space
            {
                masterSheet.Rows[iterator].Insert();
            }

            Excel.Range oldRange = oldSheet.Range["A22:H" + (22 + numItems)];
            Excel.Range masterRange = masterSheet.Cells.Range["A" + iterator + ":H" + (iterator + numItems)];
            oldRange.Copy();
            masterRange.PasteSpecial(Paste: XlPasteType.xlPasteValues);

            for (int i = 0; i <= numItems; i++) // Loop is for formatting
            {
                masterSheet.Cells.Range["B" + (iterator + i) + ":E" + (iterator + i)].Merge();

                Excel.Range BRange = masterSheet.Cells.Range["B" + (iterator + i)];

                // Make new height same as old height
                BRange.Rows.RowHeight = oldSheet.Range["B" + (22 + i)].RowHeight;

                if (BRange.Text == "Mounting Hardware:")
                {
                    BRange.Font.Bold = true;
                }
            }
        }

        // Adds order description, used in Add() function
        private static void AddDesc(Excel.Worksheet masterSheet, Excel.Worksheet oldSheet, int iterator)
        {
            int spacesForDesc = 5; // Number of spaces to add for order description
            for (int i = 0; i < spacesForDesc; i++)
            {
                masterSheet.Rows[iterator].Insert();
                masterSheet.Rows[iterator].RowHeight = 12.75;
                masterSheet.Rows[iterator].VerticalAlignment = Excel.XlVAlign.xlVAlignBottom;

                Excel.Range ARange = masterSheet.Range["A" + iterator];
                ARange.HorizontalAlignment = Excel.XlHAlign.xlHAlignLeft;
                Excel.Range BRange = masterSheet.Range["B" + iterator];
                BRange.HorizontalAlignment = Excel.XlHAlign.xlHAlignRight;
                Excel.Range DRange = masterSheet.Range["D" + iterator];
                DRange.HorizontalAlignment = Excel.XlHAlign.xlHAlignRight;
            }

            oldSheet.Range["A16:E18"].Copy();
            masterSheet.Range["A" + (iterator + 1) + ":E" + (iterator + 3)].PasteSpecial(Paste: XlPasteType.xlPasteValues);
        }

        // Locks in values instead of using formulas, used in Create() function
        private static void CreateCopyVals(Excel.Worksheet quoteSheet, Excel.Worksheet newSheet, int row)
        {
            newSheet.Range["A8"].Value = quoteSheet.Range["A8"].Value;
            newSheet.Range["A9"].Value = quoteSheet.Range["A9"].Value;
            newSheet.Range["A11:E14"].Value = quoteSheet.Range["A11:E14"].Value;
            newSheet.Range["A38"].Value = quoteSheet.Range["A38"].Value;
            newSheet.Range["A16:E18"].Value = quoteSheet.Range["A16:E18"].Value;
            newSheet.Range["A22:G" + row].Value = quoteSheet.Range["A22:G" + row].Value;
        }

        // Helper function to find row where items end
        private static int ItemsRow(Excel.Worksheet Sheet)
        {
            int row = 22;
            string colA = Sheet.Cells.Range["A" + row].Text;
            string colB = Sheet.Cells.Range["B" + row].Text;
            while (colA != "" || colB != "")
            {
                row++;
                colA = Sheet.Cells.Range["A" + row].Text;
                colB = Sheet.Cells.Range["B" + row].Text;
            }
            return row;
        }
    }
}
