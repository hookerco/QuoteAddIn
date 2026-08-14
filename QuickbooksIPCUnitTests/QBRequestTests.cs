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
        public void SalesOrderRequest_LetsQuickBooksAssignNextRefNumber()
        {
            var request = new TestableSalesOrderRequest(CreateOrder("Q-100"));
            var msgSetRequest = new Mock<IMsgSetRequest>();
            Mock<IQBStringType> refNumber = ConfigureSalesOrderAdd(msgSetRequest);

            // Embedded QBFC date setters do not mock cleanly; verify before that boundary.
            Assert.Throws<MissingMethodException>(() => request.BuildInto(msgSetRequest.Object));

            refNumber.Verify(value => value.SetValue(It.IsAny<string>()), Times.Never);
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

        [Test]
        public void CustomerAccountNumberQueryRequest_LimitsResponseToAccountNumberAndFullName()
        {
            var request = new TestableCustomerAccountNumberQueryRequest("TEST-4242");
            var msgSetRequest = new Mock<IMsgSetRequest>();
            var customerQuery = new Mock<ICustomerQuery> { DefaultValue = DefaultValue.Mock };
            msgSetRequest.Setup(value => value.AppendCustomerQueryRq())
                .Returns(customerQuery.Object);

            Assert.DoesNotThrow(() => request.BuildInto(msgSetRequest.Object));

            var includeRetElements = Mock.Get(customerQuery.Object.IncludeRetElementList);
            includeRetElements.Verify(value => value.Add("AccountNumber"), Times.Once);
            includeRetElements.Verify(value => value.Add("FullName"), Times.Once);
        }

        [Test]
        public void CustomerAccountNumberQueryRequest_SkipsMissingAccountNumberAndSelectsMatch()
        {
            var request = new TestableCustomerAccountNumberQueryRequest("TEST-4242");
            var customers = new Mock<ICustomerRetList>();
            customers.SetupGet(value => value.Count).Returns(2);
            customers.Setup(value => value.GetAt(0))
                .Returns(CreateCustomerWithoutAccountNumber("Unnumbered Customer"));
            customers.Setup(value => value.GetAt(1))
                .Returns(CreateCustomer("Example Fabrication LLC", "TEST-4242"));
            var responseSet = CreateResponseSet(
                CreateResponse(
                    ENResponseType.rtCustomerQueryRs,
                    0,
                    "Status OK",
                    customers.Object));

            QBCustomer result = request.Convert(responseSet);

            Assert.AreEqual("TEST-4242", result.AccountNumber);
            Assert.AreEqual("Example Fabrication LLC", result.Name);
        }

        [Test]
        public void CommercialTermsQueryRequest_ReturnsOnlyTermAndShipMethodNames()
        {
            Type requestType = typeof(RequestFactory).Assembly.GetType(
                "QBRequestLibrary.CommercialTermsQueryRequest");
            Assert.IsNotNull(requestType, "CommercialTermsQueryRequest must exist");
            object request = FormatterServices.GetUninitializedObject(requestType);
            GC.SuppressFinalize(request);

            var standardTerms = new Mock<IStandardTermsRet>();
            standardTerms.SetupGet(value => value.Name).Returns(CreateString("Net 30").Object);
            var termChoice = new Mock<IORTermsRet>();
            termChoice.SetupGet(value => value.StandardTermsRet).Returns(standardTerms.Object);
            var terms = new Mock<IORTermsRetList>();
            terms.SetupGet(value => value.Count).Returns(1);
            terms.Setup(value => value.GetAt(0)).Returns(termChoice.Object);

            var shipMethod = new Mock<IShipMethodRet>();
            shipMethod.SetupGet(value => value.Name).Returns(CreateString("UPS Ground").Object);
            var shipMethods = new Mock<IShipMethodRetList>();
            shipMethods.SetupGet(value => value.Count).Returns(1);
            shipMethods.Setup(value => value.GetAt(0)).Returns(shipMethod.Object);

            IMsgSetResponse responseSet = CreateResponseSet(
                CreateResponse(ENResponseType.rtTermsQueryRs, 0, "Status OK", terms.Object),
                CreateResponse(ENResponseType.rtShipMethodQueryRs, 0, "Status OK", shipMethods.Object));
            var convert = requestType.GetMethod(
                "ConvertResponse",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object result = convert.Invoke(request, new object[] { responseSet });

            Assert.AreEqual(0, result.GetType().GetProperty("StatusCode").GetValue(result));
            object catalog = result.GetType().GetProperty("Data").GetValue(result);
            CollectionAssert.AreEqual(
                new[] { "Net 30" },
                (IEnumerable<string>)catalog.GetType().GetProperty("CreditTerms").GetValue(catalog));
            CollectionAssert.AreEqual(
                new[] { "UPS Ground" },
                (IEnumerable<string>)catalog.GetType().GetProperty("ShippingMethods").GetValue(catalog));
        }

        [Test]
        public void CustomerCommercialTermsQueryRequest_ReturnsAllAccountTermsForCache()
        {
            Type requestType = typeof(RequestFactory).Assembly.GetType(
                "QBRequestLibrary.CustomerCommercialTermsQueryRequest");
            Assert.IsNotNull(requestType, "CustomerCommercialTermsQueryRequest must exist");

            object request = FormatterServices.GetUninitializedObject(requestType);
            GC.SuppressFinalize(request);
            var customer = new Mock<ICustomerRet>();
            customer.SetupGet(value => value.AccountNumber).Returns(CreateString("EX-1042").Object);
            customer.SetupGet(value => value.TermsRef).Returns(CreateNamedRef("Net 30").Object);
            var customers = new Mock<ICustomerRetList>();
            customers.SetupGet(value => value.Count).Returns(1);
            customers.Setup(value => value.GetAt(0)).Returns(customer.Object);

            IMsgSetResponse responseSet = CreateResponseSet(
                CreateResponse(ENResponseType.rtCustomerQueryRs, 0, "Status OK", customers.Object));
            var convert = requestType.GetMethod(
                "ConvertResponse",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            object response = convert.Invoke(request, new object[] { responseSet });
            object data = response.GetType().GetProperty("Data").GetValue(response);
            object record = ((System.Collections.IList)data)[0];

            Assert.AreEqual(0, response.GetType().GetProperty("StatusCode").GetValue(response));
            Assert.AreEqual("EX-1042", record.GetType().GetProperty("AccountNumber").GetValue(record));
            Assert.AreEqual("Net 30", record.GetType().GetProperty("CreditTerms").GetValue(record));
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

        private static Mock<IQBBaseRef> CreateNamedRef(string fullName)
        {
            var result = new Mock<IQBBaseRef>();
            result.SetupGet(value => value.FullName).Returns(CreateString(fullName).Object);
            return result;
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

        private static IResponse CreateResponse(
            ENResponseType responseType,
            int statusCode,
            string statusMessage,
            IQBBase detail = null)
        {
            var type = new Mock<IResponseType>();
            type.Setup(t => t.GetValue()).Returns((short)responseType);

            var response = new Mock<IResponse>();
            response.SetupGet(r => r.Type).Returns(type.Object);
            response.SetupGet(r => r.StatusCode).Returns(statusCode);
            response.SetupGet(r => r.StatusMessage).Returns(statusMessage);
            response.SetupGet(r => r.Detail).Returns(detail);
            return response.Object;
        }

        private static ICustomerRet CreateCustomer(string fullName, string accountNumber)
        {
            var name = new Mock<IQBStringType>();
            name.Setup(value => value.GetValue()).Returns(fullName);
            var account = new Mock<IQBStringType>();
            account.Setup(value => value.GetValue()).Returns(accountNumber);
            var customer = new Mock<ICustomerRet>();
            customer.SetupGet(value => value.FullName).Returns(name.Object);
            customer.SetupGet(value => value.AccountNumber).Returns(account.Object);
            return customer.Object;
        }

        private static ICustomerRet CreateCustomerWithoutAccountNumber(string fullName)
        {
            var name = new Mock<IQBStringType>();
            name.Setup(value => value.GetValue()).Returns(fullName);
            var customer = new Mock<ICustomerRet>();
            customer.SetupGet(value => value.FullName).Returns(name.Object);
            customer.SetupGet(value => value.AccountNumber).Returns((IQBStringType)null);
            return customer.Object;
        }

        private static Mock<IQBStringType> CreateString(string value)
        {
            var result = new Mock<IQBStringType>();
            result.Setup(field => field.GetValue()).Returns(value);
            return result;
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

        private sealed class TestableCustomerAccountNumberQueryRequest
            : CustomerAccountNumberQueryRequest
        {
            public TestableCustomerAccountNumberQueryRequest(string accountNumber)
                : base(accountNumber)
            {
                GC.SuppressFinalize(this);
                GC.SuppressFinalize(_connection);
            }

            public void BuildInto(IMsgSetRequest msgSetRequest)
            {
                _msgSetRequest = msgSetRequest;
                BuildHelper();
            }

            public QBCustomer Convert(IMsgSetResponse responseSet)
            {
                return ConvertResponse(responseSet);
            }
        }
    }
}
