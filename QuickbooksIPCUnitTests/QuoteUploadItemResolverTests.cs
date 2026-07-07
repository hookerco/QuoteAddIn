using System.Collections.Generic;
using NUnit.Framework;
using QuickBooksIPCContracts;
using QuoteItemResolution;

namespace QuickBooksServiceLibrary.Tests
{
    [TestFixture]
    public class QuoteUploadItemResolverTests
    {
        [Test]
        public void Resolve_UsesExistingActiveCatalogItemFoundByDescriptionPartNumber()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "RB-2500A-03000, Radius Block",
                        Quantity = 2,
                        Rate = 12.5
                    }
                },
                new[]
                {
                    new QBItem { Number = "1-1000", Description = "RB-2500A-03000, Radius Block", Active = true }
                });

            Assert.AreEqual("1-1000", result.ResolvedLines[0].Number);
            Assert.IsFalse(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(0, result.ItemsToCreate.Count);
        }

        [Test]
        public void Resolve_OverrideNumberWinsAndCreatesMissingOverrideItem()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "RB-2500A-03000, Radius Block",
                        Quantity = 1,
                        Rate = 4,
                        OverrideNumber = "SPECIAL-42"
                    }
                },
                new[]
                {
                    new QBItem { Number = "1-1000", Description = "RB-2500A-03000, Radius Block", Active = true }
                });

            Assert.AreEqual("SPECIAL-42", result.ResolvedLines[0].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
            Assert.AreEqual("SPECIAL-42", result.ItemsToCreate[0].Number);
            Assert.AreEqual("Sales Income", result.ItemsToCreate[0].AccountName);
        }

        [Test]
        public void Resolve_WiperDescriptionsUseEdpNumberAsLookupKey()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "WI-2500A-03000, Inserted Wiper EDP#3819",
                        Quantity = 1,
                        Rate = 7
                    }
                },
                new[]
                {
                    new QBItem { Number = "1-5160", Description = "WI-2500A-03000, Inserted Wiper EDP#3819", Active = true }
                });

            Assert.AreEqual("1-5160", result.ResolvedLines[0].Number);
            Assert.AreEqual(0, result.ItemsToCreate.Count);
        }

        [TestCase("BB/125, Bend Body", "1-4501")]
        [TestCase("CI/125, Clamp Insert", "1-4502")]
        [TestCase("CD125, Clamp Die", "1-4503")]
        [TestCase("PD125, Pressure Die", "1-4504")]
        public void Resolve_UsesFixedMappingsForDieSetFamilies(string description, string expectedNumber)
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = description,
                        Quantity = 1,
                        Rate = 1
                    }
                },
                new List<QBItem>());

            Assert.AreEqual(expectedNumber, result.ResolvedLines[0].Number);
            Assert.IsFalse(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(0, result.ItemsToCreate.Count);
        }

        // Contrast tests for the Excel-side divergences (see NumberGeneratorCharacterizationTests
        // and DieSetItemCharacterizationTests): the server allocates the FIRST free 1-XXXX number
        // even when the reserved numbers are dense from zero, and maps die-set prefixes
        // case-insensitively.

        [Test]
        public void Resolve_DenseReservedNumbers_GeneratesFirstFreeNumber()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine { Description = "NEW-1, First New Item", Quantity = 1, Rate = 1 },
                    new QBQuoteUploadLine { Description = "NEW-2, Second New Item", Quantity = 1, Rate = 1 }
                },
                new[]
                {
                    new QBItem { Number = "1-0000", Description = "Existing zero", Active = true },
                    new QBItem { Number = "1-0001", Description = "Existing one", Active = true },
                    new QBItem { Number = "1-0002", Description = "Existing two", Active = true }
                });

            Assert.AreEqual("1-0003", result.ResolvedLines[0].Number);
            Assert.AreEqual("1-0004", result.ResolvedLines[1].Number);
        }

        [TestCase("bb/125, Bend Body", "1-4501")]
        [TestCase("ci/125, Clamp Insert", "1-4502")]
        [TestCase("cd125, Clamp Die", "1-4503")]
        [TestCase("pd125, Pressure Die", "1-4504")]
        public void Resolve_MapsDieSetFamiliesCaseInsensitively(string description, string expectedNumber)
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine { Description = description, Quantity = 1, Rate = 1 }
                },
                new List<QBItem>());

            Assert.AreEqual(expectedNumber, result.ResolvedLines[0].Number);
            Assert.AreEqual(0, result.ItemsToCreate.Count);
        }

        [Test]
        public void Resolve_GeneratesFirstAvailableOneDashNumberForNewItemsAndSkipsReservedNumbers()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine { Description = "NEW-1, First New Item", Quantity = 1, Rate = 1 },
                    new QBQuoteUploadLine { Description = "NEW-2, Second New Item", Quantity = 1, Rate = 1 }
                },
                new[]
                {
                    new QBItem { Number = "1-0000", Description = "Existing zero", Active = true },
                    new QBItem { Number = "1-0002", Description = "Existing two", Active = true },
                    new QBItem { Number = "1-9999", Description = "Inactive still reserves number", Active = false }
                });

            Assert.AreEqual("1-0001", result.ResolvedLines[0].Number);
            Assert.AreEqual("1-0003", result.ResolvedLines[1].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.IsTrue(result.ResolvedLines[1].CreatedItem);
            Assert.AreEqual(2, result.ItemsToCreate.Count);
        }

        [Test]
        public void Resolve_ReusesGeneratedItemForRepeatedMissingDescriptionInSamePass()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine { Description = "NEW-1, First New Item", Quantity = 1, Rate = 1 },
                    new QBQuoteUploadLine { Description = "NEW-1, First New Item", Quantity = 2, Rate = 3 }
                },
                new List<QBItem>());

            Assert.AreEqual("1-0000", result.ResolvedLines[0].Number);
            Assert.AreEqual("1-0000", result.ResolvedLines[1].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.IsFalse(result.ResolvedLines[1].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
            Assert.AreEqual("1-0000", result.ItemsToCreate[0].Number);
        }

        [Test]
        public void Resolve_GeneratedNumbersSkipMissingOverrideNumbersReservedInSamePass()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "OVERRIDE-1, Override Item",
                        Quantity = 1,
                        Rate = 1,
                        OverrideNumber = "1-0000"
                    },
                    new QBQuoteUploadLine
                    {
                        Description = "NEW-1, Generated Item",
                        Quantity = 1,
                        Rate = 1
                    }
                },
                new List<QBItem>());

            Assert.AreEqual("1-0000", result.ResolvedLines[0].Number);
            Assert.AreEqual("1-0001", result.ResolvedLines[1].Number);
            Assert.AreEqual("1-0000", result.ItemsToCreate[0].Number);
            Assert.AreEqual("1-0001", result.ItemsToCreate[1].Number);
        }

        [Test]
        public void Resolve_DoesNotReuseInactiveCatalogItems()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "RB-2500A-03000, Radius Block",
                        Quantity = 1,
                        Rate = 1
                    }
                },
                new[]
                {
                    new QBItem { Number = "1-1000", Description = "RB-2500A-03000, Radius Block", Active = false }
                });

            Assert.AreEqual("1-0000", result.ResolvedLines[0].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
        }

        [Test]
        public void Resolve_DoesNotMatchWiLineToDieCatalogItemWhoseDescriptionLeadsWithWiToken()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "WI-2500A-03000, Inserted Wiper EDP#3819",
                        Quantity = 1,
                        Rate = 7
                    }
                },
                new[]
                {
                    new QBItem { Number = "1-5160", Description = "WI-2500A-03000, Inserted Wiper Die EDP#3819", Active = true }
                });

            Assert.AreNotEqual("1-5160", result.ResolvedLines[0].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
        }

        [Test]
        public void Resolve_EdpLookupMatchesWholeNumberNotDigitSubstring()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "WI-2500A-03000, Inserted Wiper EDP#3819",
                        Quantity = 1,
                        Rate = 7
                    }
                },
                new[]
                {
                    new QBItem { Number = "1-1000", Description = "WI-9999X-00000, Inserted Wiper EDP#38190", Active = true }
                });

            Assert.AreNotEqual("1-1000", result.ResolvedLines[0].Number);
            Assert.IsTrue(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(1, result.ItemsToCreate.Count);
        }

        [Test]
        public void Resolve_MatchesWiLineToInsertWhoseDescriptionMentionsDieButIsNotAWiperDie()
        {
            var result = QuoteUploadItemResolver.Resolve(
                new[]
                {
                    new QBQuoteUploadLine
                    {
                        Description = "WI-2500A-03000, Wiper Insert for Die Set EDP#3819",
                        Quantity = 1,
                        Rate = 7
                    }
                },
                new[]
                {
                    new QBItem { Number = "1-5159", Description = "WI-2500A-03000, Wiper Insert for Die Set EDP#3819", Active = true }
                });

            Assert.AreEqual("1-5159", result.ResolvedLines[0].Number);
            Assert.IsFalse(result.ResolvedLines[0].CreatedItem);
            Assert.AreEqual(0, result.ItemsToCreate.Count);
        }
    }
}
