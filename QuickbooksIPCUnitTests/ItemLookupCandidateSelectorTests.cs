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

        [Test]
        public void SelectBestItemNumber_DoesNotAllowWiWorkbookItemToMatchDieCandidateWhoseDescriptionLeadsWithWiToken()
        {
            // A wiper die is frequently labeled in QuickBooks with the paired insert's "WI-" part
            // number as its leading token, so the kind must be judged from the "DIE" wording, not the
            // leading token alone. Otherwise a WI insert line resolves to the WD die that shares its EDP.
            List<ItemLookupCandidate> candidates = new List<ItemLookupCandidate>
            {
                new ItemLookupCandidate("1-5160", "WI-2500A-03000, INSERTED WIPER DIE EDP#3819", 0)
            };

            string selected = ItemLookupCandidateSelector.SelectBestItemNumber(candidates, "3819", "WI-2500A-03000");

            Assert.AreEqual("", selected);
        }

        [Test]
        public void SelectBestItemNumber_PrefersTrueInsertOverDieThatSharesEdpAndLeadsWithWiToken()
        {
            List<ItemLookupCandidate> candidates = new List<ItemLookupCandidate>
            {
                new ItemLookupCandidate("1-5160", "WI-2500A-03000, INSERTED WIPER DIE EDP#3819", 0),
                new ItemLookupCandidate("1-5159", "WI-2500A-03000, INSERTED WIPER EDP#3819", 1)
            };

            string selected = ItemLookupCandidateSelector.SelectBestItemNumber(candidates, "3819", "WI-2500A-03000");

            Assert.AreEqual("1-5159", selected);
        }

        [Test]
        public void SelectBestItemNumber_MatchesWiInsertWhoseDescriptionMentionsDieButIsNotAWiperDie()
        {
            // An insert that merely references a die ("for die set") must still match its own item.
            // Only the product-type phrase "wiper die" marks a die, not the bare word "die".
            List<ItemLookupCandidate> candidates = new List<ItemLookupCandidate>
            {
                new ItemLookupCandidate("1-5159", "WI-2500A-03000, WIPER INSERT FOR DIE SET EDP#3819", 0)
            };

            string selected = ItemLookupCandidateSelector.SelectBestItemNumber(candidates, "3819", "WI-2500A-03000");

            Assert.AreEqual("1-5159", selected);
        }
    }
}
