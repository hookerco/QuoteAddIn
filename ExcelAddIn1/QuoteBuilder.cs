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

namespace ExcelAddIn1
{
    internal static class QuoteBuilder
    {
        public static void Create()
        {
            Excel.Worksheet quoteSheet = Globals.ThisAddIn.Application.ActiveSheet;

            quoteSheet.Copy();

            Excel.Worksheet newSheet = Globals.ThisAddIn.Application.ActiveSheet;

            try
            {
                newSheet.Shapes.Item("Button 1").Delete();
                newSheet.Shapes.Item("Button 2").Delete();
                newSheet.Shapes.Item("Button 3").Delete();
            }
            catch { }

            removeZeros();

        }
        public static void Add()
        {

        }

        private static void removeZeros()
        {
            Excel.Worksheet sheet = Globals.ThisAddIn.Application.ActiveSheet;
            int iterator = 22; // Start of items
            string colA = sheet.Range["A" + iterator].Text;
            string colB = sheet.Range["B" + iterator].Text;
            string colF = sheet.Range["F" + iterator].Text;

            int currNum = 1;

            // while the cells aren't empty (may have to change this depending on format)
            while (colA != "" || colB != "")
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
                colB = sheet.Range["B" + iterator].Text;
                colF = sheet.Range["F" + iterator].Text;
            }
        }
    }
}
