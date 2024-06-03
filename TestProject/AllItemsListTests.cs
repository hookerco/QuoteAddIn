
using System.Diagnostics;

namespace TestProject
{
	[TestClass]
	public class AllItemsListTests
	{
		[TestMethod]
		public void TestEmptyList()
		{
			(string name, string desc) = ExcelAddIn1.AllItemList.FindPart("MCDALDF", new List<string[]>());
			Debug.WriteLine($"Name: {name}, Desc: {desc}");
			Assert.AreEqual(name, "");
			Assert.AreEqual(desc, "");
		}
	}
}