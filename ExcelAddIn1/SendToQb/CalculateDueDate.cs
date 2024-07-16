using System;
using PublicHoliday;

namespace ExcelAddIn1
{
    internal static class CalculateDueDate
    {
        internal static string calculateDueDate(int weeks)
        {
            DateTime dueDateDT = DateTime.Now.AddDays(weeks * 7);
            dueDateDT = new USAPublicHoliday().NextWorkingDay(dueDateDT);
            string dueDate = dueDateDT.Date.ToString("d");
            return dueDate;
        }
    }




}
