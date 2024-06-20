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
			string name = AllItemList.FindSerialNumber("MCDALDF", ref list);
			Debug.WriteLine($"Name: {name}");
			Assert.AreEqual(name, "");
		}
	}
}