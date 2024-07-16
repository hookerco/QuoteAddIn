using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PublicHoliday;

namespace ScratchworkApp
{
    internal class Program
    {
        static void Main(string[] args)
        {
            IList<DateTime> result = new USAPublicHoliday().PublicHolidays(2024);

            foreach (DateTime holiday in result)
            {
                Console.WriteLine($"{holiday.ToString()} : {new USAPublicHoliday().NextWorkingDay(holiday).ToString()}");
            }

            return;
        }
    }
}
