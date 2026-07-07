using System.Collections.Generic;

namespace ExcelAddIn1
{
	internal class NumberGenerator
	{
		private readonly SortedSet<int> sortedNumberSet = new SortedSet<int>();

		internal NumberGenerator(IEnumerable<int> reservedNumbers)
		{
			foreach (int reservedNumber in reservedNumbers)
			{
				sortedNumberSet.Add(reservedNumber);
			}
		}

		internal string Generate()
		{
			int count = 0;
			foreach (int partNum in sortedNumberSet)
			{
				if (count != partNum)
				{
					sortedNumberSet.Add(count);
					return "1-" + count.ToString("D4");
				}
				count++;
			}
			sortedNumberSet.Add(sortedNumberSet.Count);

			string num = "1-" + sortedNumberSet.Count.ToString("D4");
			return num;
		}
	}
}
