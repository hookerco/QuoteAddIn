using Microsoft.Office.Tools.Ribbon;
using Excel = Microsoft.Office.Interop.Excel;
using Microsoft.Office.Tools.Excel;
using Microsoft.Office.Tools.Excel.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Interop.QBFC14;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Office.Interop.Excel;
using System.Diagnostics;

namespace ExcelAddIn1
{

    public partial class QBRibbon
    {
        private bool isLoaded = false;
        AllItemList allItemList = new AllItemList();
        private void Ribbon1_Load(object sender, RibbonUIEventArgs e)
        {

            // Pre-loads item list (May want to change to load when load button is pressed)
            allItemList.query_items();
            isLoaded = true;
        }

        private void button1_Click(object sender, RibbonControlEventArgs e)
        {
            if (!isLoaded) 
            {
                MessageBox.Show("Loading items. Please be patient");
            }
            else
            {
                
                QuoteUtility quoteUtility = new QuoteUtility();
                quoteUtility.allItemList = allItemList;
                MessageBox.Show("Items Loaded");
                quoteUtility.WalkItems(); 
            }
        }
    }
}
