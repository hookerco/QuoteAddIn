
using System.Diagnostics;

namespace TestProject
{
	[TestClass]
	public class AllItemsListTests
	{
		[TestMethod]
		public void TestEmptyList()
		{
			List<string[]> list = new List<string[]>();
			(string name, string desc) = ExcelAddIn1.AllItemList.FindPart("MCDALDF", ref list);
			Debug.WriteLine($"Name: {name}, Desc: {desc}");
			Assert.AreEqual(name, "");
			Assert.AreEqual(desc, "");
		}
	}
}