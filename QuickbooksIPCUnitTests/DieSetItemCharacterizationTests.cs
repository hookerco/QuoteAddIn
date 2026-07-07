using ExcelAddIn1;
using NUnit.Framework;

namespace QuickBooksServiceLibrary.Tests
{
    /// <summary>
    /// Characterization tests for the Excel-side DieSetItem mapping. The fixed die-set
    /// family mappings agree with the server-side resolver, but the add-in matches the
    /// prefixes CASE-SENSITIVELY while the server matches them case-insensitively.
    /// </summary>
    [TestFixture]
    public class DieSetItemCharacterizationTests
    {
        [TestCase("BB/125", "1-4501")]
        [TestCase("CI/125", "1-4502")]
        [TestCase("CD125", "1-4503")]
        [TestCase("CDX125", "1-4503")]
        [TestCase("PD125", "1-4504")]
        [TestCase("PDX125", "1-4504")]
        public void GetPartNum_UppercasePrefixes_MatchServerBehavior(string partNumber, string expected)
        {
            Assert.AreEqual(expected, DieSetItem.GetPartNum(partNumber));
        }

        [TestCase("bb/125")]
        [TestCase("ci/125")]
        [TestCase("cd125")]
        [TestCase("pd125")]
        public void GetPartNum_LowercasePrefixes_NotRecognized_DivergesFromServer(string partNumber)
        {
            // Server behavior maps these case-insensitively to their die-set numbers;
            // the add-in returns "" and falls through to generating a new 1-XXXX item.
            Assert.AreEqual("", DieSetItem.GetPartNum(partNumber));
        }
    }
}
