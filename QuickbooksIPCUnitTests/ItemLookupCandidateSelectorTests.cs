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

        [Test]
        public void SelectBestItemNumber_DoesNotAllowWiWorkbookItemToMatchWdCandidateWithSameEdp()
        {
            List<ItemLookupCandidate> candidates = new List<ItemLookupCandidate>
            {
                new ItemLookupCandidate("1-100", "RB-2500A-03000, Radius Block EDP#3819", 0),
                new ItemLookupCandidate("1-5160", "WD-2500A-03000, inserted wiper die EDP#3819", 1)
            };

            string selected = ItemLookupCandidateSelector.SelectBestItemNumber(candidates, "3819", "WI-2500A-03000");

            Assert.AreEqual("", selected);
        }

        [Test]
        public void SelectBestItemNumber_AllowsWdWorkbookItemToMatchEquivalentWdCandidate()
        {
            List<ItemLookupCandidate> candidates = new List<ItemLookupCandidate>
            {
                new ItemLookupCandidate("1-5160", "WD-2500A-03000, inserted wiper die EDP#3819", 0)
            };

            string selected = ItemLookupCandidateSelector.SelectBestItemNumber(candidates, "3819", "WD-2500A-03000");

            Assert.AreEqual("1-5160", selected);
        }

        [Test]
        public void SelectBestItemNumber_ReturnsNoMatchForWiWorkbookItemWhenSameEdpHasNoWiOrWdCandidate()
        {
            List<ItemLookupCandidate> candidates = new List<ItemLookupCandidate>
            {
                new ItemLookupCandidate("1-100", "RB-2500A-03000, Radius Block EDP#3819", 0),
                new ItemLookupCandidate("1-200", "PD-2500A-03000, Pressure Die EDP#3819", 1)
            };

            string selected = ItemLookupCandidateSelector.SelectBestItemNumber(candidates, "3819", "WI-2500A-03000");

            Assert.AreEqual("", selected);
        }
    }
}
