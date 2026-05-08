using System.Collections.Generic;
using NUnit.Framework;

namespace ExcelAddIn1.Tests
{
    [TestFixture]
    public class ItemLookupCandidateSelectorTests
    {
        [Test]
        public void SelectBestItemNumber_PrefersCandidateWithMatchingDescriptivePartNumber()
        {
            List<ItemLookupCandidate> candidates = new List<ItemLookupCandidate>
            {
                new ItemLookupCandidate("1-100", "WI-999, Wiper Insert EDP#1234", 0),
                new ItemLookupCandidate("1-200", "WI-123, Wiper Insert EDP#1234", 1)
            };

            string selected = ItemLookupCandidateSelector.SelectBestItemNumber(candidates, "1234", "WI-123");

            Assert.AreEqual("1-200", selected);
        }

        [Test]
        public void SelectBestItemNumber_UsesClosestDescriptivePartNumberWhenNoExactMatchExists()
        {
            List<ItemLookupCandidate> candidates = new List<ItemLookupCandidate>
            {
                new ItemLookupCandidate("1-100", "WI-999, Wiper Insert EDP#1234", 0),
                new ItemLookupCandidate("1-200", "WI-124, Wiper Insert EDP#1234", 1)
            };

            string selected = ItemLookupCandidateSelector.SelectBestItemNumber(candidates, "1234", "WI-123");

            Assert.AreEqual("1-200", selected);
        }

        [Test]
        public void SelectBestItemNumber_UsesEdpItemNumberAsTieBreaker()
        {
            List<ItemLookupCandidate> candidates = new List<ItemLookupCandidate>
            {
                new ItemLookupCandidate("1-100", "WI-124, Wiper Insert EDP#1234", 0),
                new ItemLookupCandidate("1234", "WI-122, Wiper Insert EDP#1234", 1)
            };

            string selected = ItemLookupCandidateSelector.SelectBestItemNumber(candidates, "1234", "WI-123");

            Assert.AreEqual("1234", selected);
        }

        [Test]
        public void SelectBestItemNumber_KeepsOriginalOrderWhenCandidateScoresTie()
        {
            List<ItemLookupCandidate> candidates = new List<ItemLookupCandidate>
            {
                new ItemLookupCandidate("1-100", "WI-124, Wiper Insert EDP#1234", 0),
                new ItemLookupCandidate("1-200", "WI-122, Wiper Insert EDP#1234", 1)
            };

            string selected = ItemLookupCandidateSelector.SelectBestItemNumber(candidates, "1234", "WI-123");

            Assert.AreEqual("1-100", selected);
        }
    }
}
