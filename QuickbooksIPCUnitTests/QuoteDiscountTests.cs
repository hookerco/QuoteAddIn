using System;
using System.Collections.Generic;
using Interop.QBFC14;
using Moq;
using NUnit.Framework;
using QBRequestLibrary;
using QuickBooksConnectorCore;
using QuickBooksIPCContracts;
using QuickBooksIPCService;

namespace QuickBooksServiceLibrary.Tests
{
    [TestFixture]
    public class QuoteDiscountTests
    {
        private const string Xml = "<QBXML><QBXMLMsgsRs><ItemDiscountQueryRs statusCode='0'><ItemDiscountRet><ListID>D-TEST</ListID><FullName>Synthetic Discount</FullName><IsActive>true</IsActive></ItemDiscountRet></ItemDiscountQueryRs><ItemSubtotalQueryRs statusCode='0'><ItemSubtotalRet><ListID>S-TEST</ListID><Name>Synthetic Subtotal</Name><IsActive>true</IsActive></ItemSubtotalRet></ItemSubtotalQueryRs></QBXMLMsgsRs></QBXML>";

        [Test]
        public void ItemLookupAcceptsOnlyTheConfiguredNativeItemTypes()
        {
            var result = QuoteDiscountItemsQueryRequest.Parse(Xml, "Synthetic Discount", "Synthetic Subtotal");
            Assert.AreEqual(0, result.StatusCode);
            Assert.AreEqual("D-TEST", result.Data.DiscountItemListId);
            Assert.AreEqual("S-TEST", result.Data.SubtotalItemListId);
        }

        [TestCase("true", "false")]
        [TestCase("statusCode='0'", "statusCode='1'")]
        [TestCase("ItemDiscountRet", "ItemServiceRet")]
        [TestCase("Synthetic Discount", "Different Discount")]
        [TestCase("Synthetic Subtotal", "Different Subtotal")]
        [TestCase("<ListID>D-TEST</ListID>", "")]
        public void MissingInactiveWrongTypeOrWrongNameFailsClosed(string from, string to)
        {
            var result = QuoteDiscountItemsQueryRequest.Parse(Xml.Replace(from, to), "Synthetic Discount", "Synthetic Subtotal");
            Assert.AreNotEqual(0, result.StatusCode);
            Assert.IsNull(result.Data);
        }

        [Test]
        public void MalformedXmlFailsClosed()
        {
            Assert.AreNotEqual(0, QuoteDiscountItemsQueryRequest.Parse("bad", "D", "S").StatusCode);
        }

        [Test]
        public void DiscountQueryRequestsExactNamesAndOnlyIdentityFields()
        {
            var message = new Mock<IMsgSetRequest> { DefaultValue = DefaultValue.Mock };
            var discountQuery = new Mock<IItemDiscountQuery> { DefaultValue = DefaultValue.Mock };
            var subtotalQuery = new Mock<IItemSubtotalQuery> { DefaultValue = DefaultValue.Mock };
            discountQuery.SetupGet(q => q.IncludeRetElementList).Returns(new Mock<IBSTRList>().Object);
            subtotalQuery.SetupGet(q => q.IncludeRetElementList).Returns(new Mock<IBSTRList>().Object);
            message.Setup(m => m.AppendItemDiscountQueryRq()).Returns(discountQuery.Object);
            message.Setup(m => m.AppendItemSubtotalQueryRq()).Returns(subtotalQuery.Object);
            var query = new QueryProbe();
            query.Build(message.Object);
            Mock.Get(message.Object.AppendItemDiscountQueryRq().ORListQueryWithOwnerIDAndClass.FullNameList)
                .Verify(value => value.Add("Synthetic Discount"), Times.Once);
            Mock.Get(message.Object.AppendItemSubtotalQueryRq().ORListQuery.FullNameList)
                .Verify(value => value.Add("Synthetic Subtotal"), Times.Once);
        }

        private sealed class QueryProbe : QuoteDiscountItemsQueryRequest
        {
            public QueryProbe() : base("Synthetic Discount", "Synthetic Subtotal", null) { }
            public void Build(IMsgSetRequest message) { _msgSetRequest = message; BuildHelper(); }
        }

        [Test]
        public void EstimateAppendsSubtotalThenExactDollarDiscountWithoutQuantity()
        {
            var transaction = new Mock<IEstimateAdd> { DefaultValue = DefaultValue.Mock };
            var subtotal = new Mock<IOREstimateLineAdd> { DefaultValue = DefaultValue.Mock };
            var discount = new Mock<IOREstimateLineAdd> { DefaultValue = DefaultValue.Mock };
            Mock.Get(transaction.Object.OREstimateLineAddList).SetupSequence(value => value.Append())
                .Returns(subtotal.Object).Returns(discount.Object);
            QuoteDiscountLines.Append(transaction.Object, Discount());
            Mock.Get(subtotal.Object.EstimateLineAdd.ItemRef.ListID).Verify(value => value.SetValue("S-TEST"), Times.Once);
            Mock.Get(discount.Object.EstimateLineAdd.ItemRef.ListID).Verify(value => value.SetValue("D-TEST"), Times.Once);
            Mock.Get(discount.Object.EstimateLineAdd.ORRate.Rate).Verify(value => value.SetValue(13), Times.Once);
            Mock.Get(discount.Object.EstimateLineAdd.Quantity).Verify(value => value.SetValue(It.IsAny<double>()), Times.Never);
            Mock.Get(subtotal.Object.EstimateLineAdd.Amount).Verify(value => value.SetValue(It.IsAny<double>()), Times.Never);
        }

        [Test]
        public void SalesOrderAppendsSubtotalThenExactDollarDiscountWithoutQuantity()
        {
            var transaction = new Mock<ISalesOrderAdd> { DefaultValue = DefaultValue.Mock };
            var subtotal = new Mock<IORSalesOrderLineAdd> { DefaultValue = DefaultValue.Mock };
            var discount = new Mock<IORSalesOrderLineAdd> { DefaultValue = DefaultValue.Mock };
            Mock.Get(transaction.Object.ORSalesOrderLineAddList).SetupSequence(value => value.Append())
                .Returns(subtotal.Object).Returns(discount.Object);
            QuoteDiscountLines.Append(transaction.Object, Discount());
            Mock.Get(subtotal.Object.SalesOrderLineAdd.ItemRef.ListID).Verify(value => value.SetValue("S-TEST"), Times.Once);
            Mock.Get(discount.Object.SalesOrderLineAdd.ItemRef.ListID).Verify(value => value.SetValue("D-TEST"), Times.Once);
            Mock.Get(discount.Object.SalesOrderLineAdd.ORRatePriceLevel.Rate).Verify(value => value.SetValue(13), Times.Once);
            Mock.Get(discount.Object.SalesOrderLineAdd.Quantity).Verify(value => value.SetValue(It.IsAny<double>()), Times.Never);
            Mock.Get(subtotal.Object.SalesOrderLineAdd.Amount).Verify(value => value.SetValue(It.IsAny<double>()), Times.Never);
        }

        private static QBQuoteDiscount Discount() => new QBQuoteDiscount {
            Amount = 13m, Description = "Synthetic discount", DiscountItemListId = "D-TEST", SubtotalItemListId = "S-TEST"
        };

        [TestCase("13", false)]
        [TestCase("0", false)]
        [TestCase("-1", true)]
        [TestCase("0.001", true)]
        [TestCase("NaN", true)]
        public void ConnectorParsesAndValidatesDecimalAmount(string amount, bool fails)
        {
            string json = "{\"DiscountAmount\":\"" + amount + "\"}";
            if (fails) Assert.Throws<QuoteRequestParseException>(() => SubmitQuoteHandler.ParseQuoteUploadRequest(json));
            else Assert.AreEqual(decimal.Parse(amount), SubmitQuoteHandler.ParseQuoteUploadRequest(json).DiscountAmount);
        }

        [Test]
        public void DiscountRouteRequiresTokenAndDispatchesExactlyOnce()
        {
            int calls = 0;
            var router = new QuoteBridgeRouter("http://synthetic.invalid", "synthetic-token", body => { calls++; return "{}"; });
            Assert.AreEqual(403, router.Route("POST", "/submit-discounted-quote", "wrong", "{}").StatusCode);
            Assert.AreEqual(0, calls);
            Assert.AreEqual(400, router.Route("POST", "/submit-discounted-quote", "synthetic-token", "{}").StatusCode);
            Assert.AreEqual(0, calls);
            Assert.AreEqual(200, router.Route("POST", "/submit-discounted-quote", "synthetic-token", "{\"DiscountAmount\":\"13\"}").StatusCode);
            Assert.AreEqual(1, calls);
        }
    }

    public partial class QuickBooksServiceTests
    {
        private void ConfigureDiscountService(bool configured = true)
        {
            _service = new QuickBooksService(_mockRequestFactory.Object, new Logger(_logPath),
                () => @"C:\Synthetic\Discount Test.qbw", initialize: false,
                discountSetting: key => !configured ? "" : key == "QB_QUOTE_DISCOUNT_ITEM" ? "Synthetic Discount" : "Synthetic Subtotal");
        }

        [TestCase(QBQuoteTransactionType.Estimate)]
        [TestCase(QBQuoteTransactionType.SalesOrder)]
        public void DiscountPreflightPreservesRatesAndPassesExactAmount(QBQuoteTransactionType type)
        {
            ConfigureDiscountService();
            string fingerprint = QuickBooksService.FingerprintCompanyFileName(@"C:\Synthetic\Discount Test.qbw");
            var request = QuoteRequest(type, "normal");
            request.Lines[0].Rate = 123.45;
            request.DiscountAmount = 13m;
            request.DiscountDescription = "10% rounded up";
            var calls = new List<string>();
            if (type == QBQuoteTransactionType.Estimate) ArrangeEstimateQuery(calls, request.QuoteNumber, null);
            var query = new Mock<IQuoteDiscountItemsQueryRequest>();
            query.Setup(q => q.SendRequest()).Callback(() => calls.Add("discount-items")).Returns(
                new QBStatusResponse<QBQuoteDiscount> { StatusCode = 0, Data = new QBQuoteDiscount {
                    DiscountItemListId = "D-TEST", SubtotalItemListId = "S-TEST" } });
            _mockRequestFactory.Setup(f => f.CreateQuoteDiscountItemsQueryRequest("Synthetic Discount", "Synthetic Subtotal", fingerprint)).Returns(query.Object);
            ArrangeCustomerAndItems(calls, request);
            QBOrder captured = null;
            var success = new QBStatusResponse<QBTransactionIdentity> { StatusCode = 0, Data = new QBTransactionIdentity { TransactionId = "TEST-TXN" } };
            if (type == QBQuoteTransactionType.Estimate)
            {
                var add = new Mock<IEstimateRequest>(); add.Setup(a => a.SendRequest()).Returns(success);
                _mockRequestFactory.Setup(f => f.CreateEstimateRequest(It.IsAny<QBOrder>(), fingerprint))
                    .Callback<QBOrder, string>((order, _) => captured = order).Returns(add.Object);
            }
            else
            {
                var add = new Mock<ISalesOrderRequest>(); add.Setup(a => a.SendRequest()).Returns(success);
                _mockRequestFactory.Setup(f => f.CreateSalesOrderRequest(It.IsAny<QBOrder>(), fingerprint))
                    .Callback<QBOrder, string>((order, _) => captured = order).Returns(add.Object);
            }
            Assert.AreEqual(0, _service.SubmitDiscountedQuote(request).StatusCode);
            Assert.AreEqual(123.45, captured.Items[0].Rate);
            Assert.AreEqual(13m, captured.Discount.Amount);
            Assert.AreEqual("D-TEST", captured.Discount.DiscountItemListId);
            Assert.Less(calls.IndexOf("discount-items"), calls.IndexOf("customer"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingDiscountSetupRejectsBeforeCustomerItemOrTransactionWrites(bool configured)
        {
            ConfigureDiscountService(configured);
            var request = QuoteRequest(QBQuoteTransactionType.SalesOrder, "normal");
            request.DiscountAmount = 1m;
            Assert.AreNotEqual(0, _service.SubmitDiscountedQuote(request).StatusCode);
            AssertNoCustomerItemOrTransactionWrites();
        }

        [TestCase(-1)]
        [TestCase(11)]
        public void InvalidDiscountRejectedBeforeAnyLookup(int amount)
        {
            ConfigureDiscountService();
            var request = QuoteRequest(QBQuoteTransactionType.Estimate, "normal");
            request.DiscountAmount = amount;
            Assert.AreNotEqual(0, _service.SubmitQuote(request).StatusCode);
            AssertNoQuoteLookupsOrWrites();
        }
    }
}
