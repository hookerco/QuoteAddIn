using NUnit.Framework;

namespace QuoteItemResolution.Tests
{
    [TestFixture]
    public class ItemLookupKeyTests
    {
        [TestCase("WI-ABC, Wiper Insert EDP#1234", "WI-ABC", "1234")]
        [TestCase("WD-ABC, Wiper Die EDP#5678", "WD-ABC", "5678")]
        [TestCase("wi-abc, wiper insert edp#1234", "wi-abc", "1234")]
        [TestCase("wd-abc, wiper die EdP#5678", "wd-abc", "5678")]
        [TestCase("WI-ABC, Wiper Insert EDP #1234", "WI-ABC", "1234")]
        [TestCase("WI-ABC, Wiper Insert EDP# 1234", "WI-ABC", "1234")]
        [TestCase("WI-ABC, Wiper Insert EDP # 1234", "WI-ABC", "1234")]
        [TestCase("WI-ABC, Wiper Insert EDP   #   1234", "WI-ABC", "1234")]
        [TestCase("WI-ABC, Wiper Insert EDP#:1234", "WI-ABC", "1234")]
        [TestCase("WI-ABC, Wiper Insert EDP # : 1234", "WI-ABC", "1234")]
        [TestCase("WI-ABC, Wiper Insert EDP#123456", "WI-ABC", "123456")]
        [TestCase("WI123, Wiper Insert EDP#1234", "WI123", "1234")]
        [TestCase("WD987, Wiper Die EDP#5678", "WD987", "5678")]
        public void GetLookupPartNumber_UsesEdpNumberForWiAndWdItems(string description, string quotePartNumber, string expected)
        {
            Assert.AreEqual(expected, ItemLookupKey.GetLookupPartNumber(description, quotePartNumber));
        }

        [TestCase("RB-ABC, Radius Block EDP#1234", "RB-ABC")]
        [TestCase("PD-ABC, Pressure Die EDP # 5678", "PD-ABC")]
        [TestCase("CL-ABC, Clamp EDP#1234", "CL-ABC")]
        public void GetLookupPartNumber_IgnoresEdpNumberForNonWiAndWdItems(string description, string quotePartNumber)
        {
            Assert.AreEqual(quotePartNumber, ItemLookupKey.GetLookupPartNumber(description, quotePartNumber));
        }

        [TestCase("WI-ABC, Wiper Insert without EDP", "WI-ABC")]
        [TestCase("WD-ABC, Wiper Die EDP# ", "WD-ABC")]
        [TestCase("WI-ABC, Wiper Insert EDP # , trailing punctuation", "WI-ABC")]
        [TestCase("WI-ABC, Wiper Insert EDP# REQUIRED", "WI-ABC")]
        public void GetLookupPartNumber_FallsBackToQuotePartNumberWhenWiOrWdHasNoEdpNumber(string description, string quotePartNumber)
        {
            Assert.AreEqual(quotePartNumber, ItemLookupKey.GetLookupPartNumber(description, quotePartNumber));
        }

        [TestCase("WIPER-ABC, Wiper Insert EDP#1234", "WIPER-ABC")]
        [TestCase("WDX-ABC, Wiper Die EDP#1234", "WDX-ABC")]
        [TestCase("AWI-ABC, Wiper Insert EDP#1234", "AWI-ABC")]
        public void GetLookupPartNumber_RequiresWiOrWdPrefixAsWholeToken(string description, string quotePartNumber)
        {
            Assert.AreEqual(quotePartNumber, ItemLookupKey.GetLookupPartNumber(description, quotePartNumber));
        }

        [TestCase("WI-2000A-04000, 2 x 4 wiper insert, alum-bronze, standard cut, EDP#3700", "WI-2000A-04000", "3700")]
        [TestCase("wi-abc, wiper insert edp#1234", "wi-abc", "1234")]
        [TestCase("WI123, Wiper Insert EDP # : 1234", "WI123", "1234")]
        [TestCase("WI-2500A-03000, Wiper Insert for Die Set EDP#3819", "WI-2500A-03000", "3819")]
        public void GetInsertEdpNumber_ReturnsEdpNumberForWiperInsertLines(
            string description, string quotePartNumber, string expected)
        {
            Assert.AreEqual(expected, ItemLookupKey.GetInsertEdpNumber(description, quotePartNumber));
        }

        [TestCase("WI-2500A-03000, Inserted Wiper Die EDP#3819", "WI-2500A-03000")]
        [TestCase("WI-2500A-03000, INSERTED WIPER DIES EDP#3819", "WI-2500A-03000")]
        [TestCase("wd-2500a-03000, inserted wiper edp#3819", "wd-2500a-03000")]
        [TestCase("WD987, Wiper Die EDP#5678", "WD987")]
        [TestCase("WI-ABC, Wiper Insert without EDP", "WI-ABC")]
        [TestCase("WI-ABC, Wiper Insert EDP# ", "WI-ABC")]
        [TestCase("RB-ABC, Radius Block EDP#1234", "RB-ABC")]
        [TestCase("WIPER-ABC, Wiper Insert EDP#1234", "WIPER-ABC")]
        public void GetInsertEdpNumber_ReturnsEmptyWhenLineIsNotAnEdpWiperInsert(
            string description, string quotePartNumber)
        {
            Assert.AreEqual("", ItemLookupKey.GetInsertEdpNumber(description, quotePartNumber));
        }
    }
}
