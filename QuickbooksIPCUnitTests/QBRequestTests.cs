using Interop.QBFC14;
using Moq;
using NUnit.Framework;
using QBRequestLibrary;
using QuickBooksIPCContracts;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace QuickBooksServiceLibrary.Tests
{
    [TestFixture]
    public class QBRequestTests
    {
        [Test]
        public void AllItemNonInvQueryRequest_WhenServiceQueryFails_ReturnsFailureStatus()
        {
            var request = CreateRequestWithoutConnectingOrLogging();
            var responseSet = CreateResponseSet(
                CreateResponse(ENResponseType.rtItemNonInventoryQueryRs, 0, "Status OK"),
                CreateResponse(ENResponseType.rtItemServiceQueryRs, 3250, "Service query failed"));

            var result = request.Convert(responseSet);

            Assert.AreEqual(3250, result.StatusCode);
            Assert.AreEqual("Service query failed", result.StatusMessage);
        }

        [Test]
        public void SalesOrderRequest_BuildsRefNumberFromQuoteNumber()
        {
            var request = new TestableSalesOrderRequest(CreateOrder("Q-100"));
            var msgSetRequest = new Mock<IMsgSetRequest>();
            Mock<IQBStringType> refNumber = ConfigureSalesOrderAdd(msgSetRequest);

            // Embedded QBFC date setters do not mock cleanly; verify before that boundary.
            Assert.Throws<MissingMethodException>(() => request.BuildInto(msgSetRequest.Object));

            refNumber.Verify(value => value.SetValue("Q-100"), Times.Once);
        }

        [Test]
        public void EstimateRequest_BuildsRefNumberFromQuoteNumber()
        {
            var request = new TestableEstimateRequest(CreateOrder("Q-200"));
            var msgSetRequest = new Mock<IMsgSetRequest>();
            Mock<IQBStringType> refNumber = ConfigureEstimateAdd(msgSetRequest);

            // Embedded QBFC date setters do not mock cleanly; verify before that boundary.
            Assert.Throws<MissingMethodException>(() => request.BuildInto(msgSetRequest.Object));

            refNumber.Verify(value => value.SetValue("Q-200"), Times.Once);
        }

        private static QBOrder CreateOrder(string quoteNumber)
        {
            return new QBOrder
            {
                QuoteNumber = quoteNumber,
                Customer = new QBCustomer { Name = "CustomerName", PO = "PO-123" },
                DueDate = new DateTime(2026, 7, 16),
                Items = new List<QBItem>
                {
                    new QBItem
                    {
                        Number = "1-1000",
                        Description = "RB-2500A-03000, Radius Block",
                        Quantity = 1,
                        Rate = 12.5
                    }
                }
            };
        }

        private static Mock<IQBStringType> ConfigureSalesOrderAdd(Mock<IMsgSetRequest> msgSetRequest)
        {
            var salesOrderAdd = new Mock<ISalesOrderAdd>();
            msgSetRequest.Setup(request => request.AppendSalesOrderAddRq()).Returns(salesOrderAdd.Object);

            var refNumber = new Mock<IQBStringType>();
            salesOrderAdd.SetupGet(request => request.RefNumber).Returns(refNumber.Object);
            ConfigureCommonOrderFields(salesOrderAdd);

            var lineList = new Mock<IORSalesOrderLineAddList>();
            var lineChoice = new Mock<IORSalesOrderLineAdd>();
            var line = new Mock<ISalesOrderLineAdd>();
            salesOrderAdd.SetupGet(request => request.ORSalesOrderLineAddList).Returns(lineList.Object);
            lineList.Setup(list => list.Append()).Returns(lineChoice.Object);
            lineChoice.SetupGet(choice => choice.SalesOrderLineAdd).Returns(line.Object);

            line.SetupGet(request => request.ItemRef).Returns(CreateBaseRef().Object);
            line.SetupGet(request => request.Desc).Returns(new Mock<IQBStringType>().Object);
            line.SetupGet(request => request.Quantity).Returns(new Mock<IQBQuanType>().Object);
            line.SetupGet(request => request.ORRatePriceLevel).Returns(CreateRatePriceLevel().Object);
            return refNumber;
        }

        private static Mock<IQBStringType> ConfigureEstimateAdd(Mock<IMsgSetRequest> msgSetRequest)
        {
            var estimateAdd = new Mock<IEstimateAdd>();
            msgSetRequest.Setup(request => request.AppendEstimateAddRq()).Returns(estimateAdd.Object);

            var refNumber = new Mock<IQBStringType>();
            estimateAdd.SetupGet(request => request.RefNumber).Returns(refNumber.Object);
            ConfigureCommonOrderFields(estimateAdd);

            var lineList = new Mock<IOREstimateLineAddList>();
            var lineChoice = new Mock<IOREstimateLineAdd>();
            var line = new Mock<IEstimateLineAdd>();
            estimateAdd.SetupGet(request => request.OREstimateLineAddList).Returns(lineList.Object);
            lineList.Setup(list => list.Append()).Returns(lineChoice.Object);
            lineChoice.SetupGet(choice => choice.EstimateLineAdd).Returns(line.Object);

            line.SetupGet(request => request.ItemRef).Returns(CreateBaseRef().Object);
            line.SetupGet(request => request.Desc).Returns(new Mock<IQBStringType>().Object);
            line.SetupGet(request => request.Quantity).Returns(new Mock<IQBQuanType>().Object);
            line.SetupGet(request => request.ORRate).Returns(CreateRate().Object);
            return refNumber;
        }

        private static void ConfigureCommonOrderFields(Mock<ISalesOrderAdd> request)
        {
            request.SetupGet(add => add.CustomerRef).Returns(CreateBaseRef().Object);
            request.SetupGet(add => add.PONumber).Returns(new Mock<IQBStringType>().Object);
            request.SetupGet(add => add.DueDate).Returns(new Mock<IQBDateType>().Object);
            request.SetupGet(add => add.ShipDate).Returns(new Mock<IQBDateType>().Object);
        }

        private static void ConfigureCommonOrderFields(Mock<IEstimateAdd> request)
        {
            request.SetupGet(add => add.CustomerRef).Returns(CreateBaseRef().Object);
            request.SetupGet(add => add.PONumber).Returns(new Mock<IQBStringType>().Object);
            request.SetupGet(add => add.TxnDate).Returns(new Mock<IQBDateType>().Object);
        }

        private static Mock<IQBBaseRef> CreateBaseRef()
        {
            var baseRef = new Mock<IQBBaseRef>();
            baseRef.SetupGet(value => value.FullName).Returns(new Mock<IQBStringType>().Object);
            return baseRef;
        }

        private static Mock<IORRatePriceLevel> CreateRatePriceLevel()
        {
            var ratePriceLevel = new Mock<IORRatePriceLevel>();
            ratePriceLevel.SetupGet(value => value.Rate).Returns(new Mock<IQBPriceType>().Object);
            return ratePriceLevel;
        }

        private static Mock<IORRate> CreateRate()
        {
            var rate = new Mock<IORRate>();
            rate.SetupGet(value => value.Rate).Returns(new Mock<IQBPriceType>().Object);
            return rate;
        }

        private static TestableAllItemNonInvQueryRequest CreateRequestWithoutConnectingOrLogging()
        {
            var request = (TestableAllItemNonInvQueryRequest)FormatterServices.GetUninitializedObject(
                typeof(TestableAllItemNonInvQueryRequest));
            GC.SuppressFinalize(request);
            return request;
        }

        private static IMsgSetResponse CreateResponseSet(params IResponse[] responses)
        {
            var responseList = new Mock<IResponseList>();
            responseList.SetupGet(l => l.Count).Returns(responses.Length);
            responseList.Setup(l => l.GetAt(It.IsAny<int>())).Returns((int i) => responses[i]);

            var responseSet = new Mock<IMsgSetResponse>();
            responseSet.SetupGet(r => r.ResponseList).Returns(responseList.Object);
            return responseSet.Object;
        }

        private static IResponse CreateResponse(ENResponseType responseType, int statusCode, string statusMessage)
        {
            var type = new Mock<IResponseType>();
            type.Setup(t => t.GetValue()).Returns((short)responseType);

            var response = new Mock<IResponse>();
            response.SetupGet(r => r.Type).Returns(type.Object);
            response.SetupGet(r => r.StatusCode).Returns(statusCode);
            response.SetupGet(r => r.StatusMessage).Returns(statusMessage);
            response.SetupGet(r => r.Detail).Returns((IQBBase)null);
            return response.Object;
        }

        private sealed class TestableAllItemNonInvQueryRequest : AllItemNonInvQueryRequest
        {
            public QBStatusResponse<List<QBItem>> Convert(IMsgSetResponse responseSet)
            {
                return ConvertResponse(responseSet);
            }
        }

        private sealed class TestableSalesOrderRequest : SalesOrderRequest
        {
            public TestableSalesOrderRequest(QBOrder salesOrder)
                : base(salesOrder)
            {
                GC.SuppressFinalize(this);
                GC.SuppressFinalize(_connection);
            }

            public void BuildInto(IMsgSetRequest msgSetRequest)
            {
                _msgSetRequest = msgSetRequest;
                BuildHelper();
            }
        }

        private sealed class TestableEstimateRequest : EstimateRequest
        {
            public TestableEstimateRequest(QBOrder estimate)
                : base(estimate)
            {
                GC.SuppressFinalize(this);
                GC.SuppressFinalize(_connection);
            }

            public void BuildInto(IMsgSetRequest msgSetRequest)
            {
                _msgSetRequest = msgSetRequest;
                BuildHelper();
            }
        }
    }
}
