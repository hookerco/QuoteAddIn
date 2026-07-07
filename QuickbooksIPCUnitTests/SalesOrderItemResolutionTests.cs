using System.Collections.Generic;
using ExcelAddIn1;
using NUnit.Framework;
using QuickBooksIPCContracts;

namespace QuickBooksServiceLibrary.Tests
{
    /// <summary>
    /// Tests for the Excel-side bridge onto the shared QuoteItemResolution resolver.
    /// These pin the deliberate behavior changes on the add-in send path:
    /// the dense-set number-generation fix, case-insensitive die-set prefixes,
    /// and same-pass reuse of a just-created item.
    /// </summary>
    [TestFixture]
    public class SalesOrderItemResolutionTests
    {
        private static SOSheetQuoteItem MakeItem(string description, int row, string overrideNumber = "")
        {
            var baseItem = new BaseQuoteItem();
            baseItem.SetDescription(description);
            baseItem.SetQuantity(1);
            baseItem.SetRate(2.5);

            var item = new SOSheetQuoteItem(baseItem, row);
            item.SetOverride(overrideNumber);
            return item;
        }

        [Test]
        public void ResolveNumbers_DenseCatalog_AssignsFirstFreeNumber()
        {
            // The old Excel NumberGenerator returned "1-0004" here (the deliberate bug fix).
            var items = new List<SOSheetQuoteItem> { MakeItem("NEW-1, First New Item", 5) };
            var catalog = new List<QBItem>
            {
                new QBItem { Number = "1-0000", Description = "Existing zero", Active = true },
                new QBItem { Number = "1-0001", Description = "Existing one", Active = true },
                new QBItem { Number = "1-0002", Description = "Existing two", Active = true }
            };

            List<QBItem> itemsToCreate = SalesOrderItemResolution.ResolveNumbers(items, catalog);

            Assert.AreEqual("1-0003", items[0].GetNumber());
            Assert.AreEqual(1, itemsToCreate.Count);
            Assert.AreEqual("1-0003", itemsToCreate[0].Number);
            Assert.AreEqual("Sales Income", itemsToCreate[0].AccountName);
        }

        [Test]
        public void ResolveNumbers_ExistingCatalogItem_ReusesNumberAndCreatesNothing()
        {
            var items = new List<SOSheetQuoteItem> { MakeItem("RB-2500A-03000, Radius Block", 5) };
            var catalog = new List<QBItem>
            {
                new QBItem { Number = "1-1000", Description = "RB-2500A-03000, Radius Block", Active = true }
            };

            List<QBItem> itemsToCreate = SalesOrderItemResolution.ResolveNumbers(items, catalog);

            Assert.AreEqual("1-1000", items[0].GetNumber());
            Assert.AreEqual(0, itemsToCreate.Count);
        }

        [Test]
        public void ResolveNumbers_OverrideWins_MissingOverrideItemIsCreated()
        {
            var items = new List<SOSheetQuoteItem> { MakeItem("RB-2500A-03000, Radius Block", 5, "SPECIAL-42") };
            var catalog = new List<QBItem>
            {
                new QBItem { Number = "1-1000", Description = "RB-2500A-03000, Radius Block", Active = true }
            };

            List<QBItem> itemsToCreate = SalesOrderItemResolution.ResolveNumbers(items, catalog);

            Assert.AreEqual("SPECIAL-42", items[0].GetNumber());
            Assert.AreEqual(1, itemsToCreate.Count);
            Assert.AreEqual("SPECIAL-42", itemsToCreate[0].Number);
        }

        [Test]
        public void ResolveNumbers_NullOverride_TreatedAsNoOverride()
        {
            var baseItem = new BaseQuoteItem();
            baseItem.SetDescription("RB-2500A-03000, Radius Block");
            baseItem.SetQuantity(1);
            baseItem.SetRate(2.5);
            var item = new SOSheetQuoteItem(baseItem, 5); // SetOverride never called

            var catalog = new List<QBItem>
            {
                new QBItem { Number = "1-1000", Description = "RB-2500A-03000, Radius Block", Active = true }
            };

            SalesOrderItemResolution.ResolveNumbers(new List<SOSheetQuoteItem> { item }, catalog);

            Assert.AreEqual("1-1000", item.GetNumber());
        }

        [Test]
        public void ResolveNumbers_AppliesNumbersToItemsInRowOrder()
        {
            var items = new List<SOSheetQuoteItem>
            {
                MakeItem("RB-2500A-03000, Radius Block", 5),
                MakeItem("BB/125, Bend Body", 6),
                MakeItem("NEW-1, Brand New Item", 7)
            };
            var catalog = new List<QBItem>
            {
                new QBItem { Number = "1-1000", Description = "RB-2500A-03000, Radius Block", Active = true }
            };

            List<QBItem> itemsToCreate = SalesOrderItemResolution.ResolveNumbers(items, catalog);

            Assert.AreEqual("1-1000", items[0].GetNumber());
            Assert.AreEqual("1-4501", items[1].GetNumber());
            Assert.AreEqual("1-0000", items[2].GetNumber());
            Assert.AreEqual(1, itemsToCreate.Count);
        }

        [Test]
        public void ResolveNumbers_LowercaseDieSetPrefix_MapsToDieSetNumber()
        {
            // The old Excel DieSetItem matched prefixes case-sensitively and would have
            // generated a brand-new 1-XXXX item for this line.
            var items = new List<SOSheetQuoteItem> { MakeItem("bb/125, Bend Body", 5) };

            List<QBItem> itemsToCreate = SalesOrderItemResolution.ResolveNumbers(items, new List<QBItem>());

            Assert.AreEqual("1-4501", items[0].GetNumber());
            Assert.AreEqual(0, itemsToCreate.Count);
        }

        [Test]
        public void ResolveNumbers_RepeatedNewDescription_ReusesSameNumberAndCreatesOnce()
        {
            // The old Excel path re-added created items as inactive, so a repeated line
            // minted a second number and created a duplicate QuickBooks item.
            var items = new List<SOSheetQuoteItem>
            {
                MakeItem("NEW-1, First New Item", 5),
                MakeItem("NEW-1, First New Item", 6)
            };

            List<QBItem> itemsToCreate = SalesOrderItemResolution.ResolveNumbers(items, new List<QBItem>());

            Assert.AreEqual("1-0000", items[0].GetNumber());
            Assert.AreEqual("1-0000", items[1].GetNumber());
            Assert.AreEqual(1, itemsToCreate.Count);
        }

        [Test]
        public void ResolveNumbers_PaddedOverrideFromSheetCell_IsTrimmedOntoItem()
        {
            var items = new List<SOSheetQuoteItem> { MakeItem("NEW-1, New Item", 5, " 1-0005 ") };

            List<QBItem> itemsToCreate = SalesOrderItemResolution.ResolveNumbers(items, new List<QBItem>());

            Assert.AreEqual("1-0005", items[0].GetNumber());
            Assert.AreEqual(1, itemsToCreate.Count);
            Assert.AreEqual("1-0005", itemsToCreate[0].Number);
        }

        // GetInputNumber feeds the audit record's "number" field; it must mirror the
        // resolver's override semantics (trim; whitespace-only means no override) so the
        // audit reflects the number QuickBooks actually received.

        [Test]
        public void GetInputNumber_PaddedOverride_ReturnsTrimmedOverride()
        {
            var item = MakeItem("NEW-1, New Item", 5, " 1-0005 ");

            Assert.AreEqual("1-0005", item.GetInputNumber());
        }

        [Test]
        public void GetInputNumber_WhitespaceOnlyOverride_ReturnsResolvedNumber()
        {
            var item = MakeItem("NEW-1, New Item", 5, "   ");
            item.SetNumber("1-0000");

            Assert.AreEqual("1-0000", item.GetInputNumber());
        }

        [Test]
        public void GetInputNumber_NullOverride_ReturnsResolvedNumber()
        {
            var baseItem = new BaseQuoteItem();
            baseItem.SetDescription("NEW-1, New Item");
            var item = new SOSheetQuoteItem(baseItem, 5); // SetOverride never called
            item.SetNumber("1-0000");

            Assert.AreEqual("1-0000", item.GetInputNumber());
        }

        [Test]
        public void ResolveNumbers_DescriptionWithoutComma_ResolvesInsteadOfThrowing()
        {
            // The old Excel path threw NotPartException and aborted the send.
            var items = new List<SOSheetQuoteItem> { MakeItem("Justadescription", 5) };

            List<QBItem> itemsToCreate = SalesOrderItemResolution.ResolveNumbers(items, new List<QBItem>());

            Assert.AreEqual("1-0000", items[0].GetNumber());
            Assert.AreEqual(1, itemsToCreate.Count);
        }
    }
}
