using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Excel = Microsoft.Office.Interop.Excel;

namespace QuoteBuilder
{
    public class QuoteSheet
    {
        public int lastRow { get; private set; }
        private Excel.Worksheet sheet;
    }
}
