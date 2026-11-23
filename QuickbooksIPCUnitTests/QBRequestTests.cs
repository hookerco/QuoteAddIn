using Interop.QBFC14;
using Moq;
using NUnit.Framework;
using QBRequestLibrary;
using QuickBooksIPCContracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;

namespace QuickBooksServiceLibrary.Tests
{
    [TestFixture]
    public class QBRequestTests
    {
        [Test]
        public void QuoteIdentity_LoggerDefaultPathUsesEnvironmentOverride()
        {
            const string variable = "QUOTEADDIN_LOG_PATH";
            const string expected = @"C:\Temp\quoteaddin-unit-tests.log";
            string previous = Environment.GetEnvironmentVariable(variable);

            try
            {
                Environment.SetEnvironmentVariable(variable, expected);

                Assert.AreEqual(expected, Logger.DefaultLogFilePath());
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
            }
        }

        [Test]
        public void QuoteIdentity_ConnectionReadsCompanyFileOnlyAfterSessionBegins()
        {
            var calls = new List<string>();
            var sessionManager = new Mock<IQBSessionManager>();
            sessionManager.Setup(value => value.OpenConnection("", "Proto-CAM QB Library"))
                .Callback(() => calls.Add("open"));
            sessionManager.Setup(value => value.BeginSession("", ENOpenMode.omDontCare))
                .Callback(() => calls.Add("begin"));
            sessionManager.Setup(value => value.GetCurrentCompanyFileName())
                .Callback(() => calls.Add("identity"))
                .Returns(@"C:\Synthetic\Test Company.qbw");
            using (var connection = new TestableConnection(sessionManager.Object))
            {
                Assert.IsTrue(connection.Open());
                CollectionAssert.AreEqual(new[] { "open", "begin", "identity" }, calls);
                Assert.AreEqual(@"C:\Synthetic\Test Company.qbw", connection.CurrentCompanyFileName);
            }
        }

        [TestCase("item")]
        [TestCase("estimate")]
        [TestCase("estimate-query")]
        [TestCase("sales-order")]
        public void QuoteWriteSafety_WriteRequestRechecksCompanyBeforeBuildOrDoRequests(
            string requestKind)
        {
            const string approvedFingerprint =
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var sessionManager = new Mock<IQBSessionManager>();
            sessionManager.Setup(value => value.OpenConnection("", "Proto-CAM QB Library"));
            sessionManager.Setup(value => value.BeginSession("", ENOpenMode.omDontCare));
            sessionManager.Setup(value => value.GetCurrentCompanyFileName())
                .Returns(@"C:\Synthetic\Different Company.qbw");
            sessionManager.Setup(value => value.EndSession());
            sessionManager.Setup(value => value.CloseConnection());
            var connection = new TestableConnection(sessionManager.Object);
            TestDelegate send;

            if (requestKind == "item")
            {
                var request = new TestableAddItemNonInventoryRequest(
                    new List<QBItem> { new QBItem { Number = "1-9000" } },
                    approvedFingerprint,
                    connection);
                send = () => request.SendRequest();
            }
            else if (requestKind == "estimate")
            {
                var request = new TestableEstimateRequest(
                    CreateOrder("TEST-ABC234"),
                    approvedFingerprint,
                    connection);
                send = () => request.SendRequest();
            }
            else if (requestKind == "sales-order")
            {
                var request = new TestableSalesOrderRequest(
                    CreateOrder("TEST-DEF567"),
                    approvedFingerprint,
                    connection);
                send = () => request.SendRequest();
            }
            else
            {
                var request = new TestableEstimateReferenceQueryRequest(
                    "TEST-ABC234",
                    approvedFingerprint,
                    connection);
                send = () => request.SendRequest();
            }

            Exception error = Assert.Catch<Exception>(send);

            Assert.AreEqual("QuickBooks company verification failed.", error.Message);
            sessionManager.Verify(
                value => value.CreateMsgSetRequest(It.IsAny<string>(), It.IsAny<short>(), It.IsAny<short>()),
                Times.Never);
            sessionManager.Verify(
                value => value.DoRequests(It.IsAny<IMsgSetRequest>()),
                Times.Never);
        }

        [Test]
        public void QuoteWriteSafety_EstimateReferenceQueryFailureLogsOnlyGenericMessage()
        {
            const string variable = "QUOTEADDIN_LOG_PATH";
            const string rawDetail = @"QBFC failed for C:\Synthetic\Private Company.qbw TXN-PRIVATE";
            string previous = Environment.GetEnvironmentVariable(variable);
            string logPath = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "logs",
                Guid.NewGuid().ToString("N") + ".log");

            try
            {
                Environment.SetEnvironmentVariable(variable, logPath);
                var request = new FailingEstimateReferenceQueryRequest(rawDetail);

                InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                    () => request.SendRequest());

                Assert.AreEqual(rawDetail, error.Message);
                string log = File.ReadAllText(logPath);
                StringAssert.Contains("QuickBooks Estimate reference query failed.", log);
                StringAssert.DoesNotContain(rawDetail, log);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
            }
        }

        [Test]
        public void AllItemNonInvQueryRequest_WhenServiceQueryFails_ReturnsFailureStatus()
        {
            var result = ParseCache<QBStatusResponse<List<QBItem>>>("AllItemNonInvQueryRequest",
                "<ItemNonInventoryQueryRs statusCode='0' statusMessage='OK'/><ItemServiceQueryRs statusCode='3250' statusMessage='Service query failed'/>");
            Assert.AreEqual(3250, result.StatusCode);
            Assert.AreEqual("Service query failed", result.StatusMessage);
            Assert.IsNull(result.Data);
        }
        [Test]
        public void QuoteIdentity_SalesOrderRequest_LetsQuickBooksAssignNextRefNumber()
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
        public void QuoteIdentity_EstimateReferenceQueryRequestsOnlyIdentityFieldsAndExactMatch()
        {
            var request = new TestableEstimateReferenceQueryRequest("120050");
            var msgSetRequest = new Mock<IMsgSetRequest>();
            var query = new Mock<IEstimateQuery>();
            var includeRetElements = new Mock<IBSTRList>();
            var txnQuery = new Mock<IORTxnQuery>();
            var refNumbers = new Mock<IBSTRList>();
            msgSetRequest.Setup(value => value.AppendEstimateQueryRq()).Returns(query.Object);
            query.SetupGet(value => value.IncludeRetElementList).Returns(includeRetElements.Object);
            query.SetupGet(value => value.ORTxnQuery).Returns(txnQuery.Object);
            txnQuery.SetupGet(value => value.RefNumberList).Returns(refNumbers.Object);

            request.BuildInto(msgSetRequest.Object);

            includeRetElements.Verify(value => value.Add("TxnID"), Times.Once);
            includeRetElements.Verify(value => value.Add("RefNumber"), Times.Once);
            includeRetElements.Verify(value => value.Add(It.IsAny<string>()), Times.Exactly(2));
            refNumbers.Verify(value => value.Add("120050"), Times.Once);
        }

        [TestCase("120050", 1)]
        [TestCase(null, 1)]
        [TestCase("120050", 500)]
        public void QuoteIdentity_EstimateNoMatchIsSuccessfulEmptyResult(string reference, int statusCode)
        {
            var request = new TestableEstimateReferenceQueryRequest(reference);
            var result = request.Convert(CreateResponseSet(
                CreateResponse(ENResponseType.rtEstimateQueryRs, statusCode, "No match")));

            Assert.AreEqual(0, result.StatusCode);
            Assert.IsNotNull(result.Data);
            Assert.IsEmpty(result.Data);
        }

        [TestCase(3250)]
        [TestCase(-1)]
        public void QuoteIdentity_EstimateLookupErrorsRemainFailures(int statusCode)
        {
            var request = new TestableEstimateReferenceQueryRequest("120050");
            var result = request.Convert(CreateResponseSet(
                CreateResponse(ENResponseType.rtEstimateQueryRs, statusCode, "Query failed")));

            Assert.AreEqual(statusCode, result.StatusCode);
            Assert.AreEqual("Query failed", result.StatusMessage);
        }

        [Test]
        public void QuoteIdentity_EstimateScanObjectNotFoundRemainsFailure()
        {
            var request = new TestableEstimateReferenceQueryRequest(null);
            var result = request.Convert(CreateResponseSet(
                CreateResponse(ENResponseType.rtEstimateQueryRs, 500, "Object not found")));

            Assert.AreEqual(500, result.StatusCode);
        }

        [TestCase(1)]
        [TestCase(500)]
        public void QuoteIdentity_EstimateNoMatchWithReturnedEstimateRemainsFailure(int statusCode)
        {
            var request = new TestableEstimateReferenceQueryRequest("120050");
            var estimates = new Mock<IEstimateRetList>();
            estimates.SetupGet(value => value.Count).Returns(1);
            estimates.Setup(value => value.GetAt(0)).Returns(CreateEstimate("120050", "TXN-1"));
            var result = request.Convert(CreateResponseSet(
                CreateResponse(ENResponseType.rtEstimateQueryRs, statusCode, "No match", estimates.Object)));

            Assert.AreEqual(statusCode, result.StatusCode);
            Assert.AreEqual(1, result.Data.Count);
        }

        [Test]
        public void QuoteIdentity_EstimateReferenceScanReturnsOnlyPositiveDecimalReferences()
        {
            var request = new TestableEstimateReferenceQueryRequest(null);
            var estimates = new Mock<IEstimateRetList>();
            estimates.SetupGet(value => value.Count).Returns(5);
            estimates.Setup(value => value.GetAt(0)).Returns(CreateEstimate("120050", "TXN-1"));
            estimates.Setup(value => value.GetAt(1)).Returns(CreateEstimate("", "TXN-2"));
            estimates.Setup(value => value.GetAt(2)).Returns(CreateEstimate("TEST-ABC123", "TXN-3"));
            estimates.Setup(value => value.GetAt(3)).Returns(CreateEstimate("0", "TXN-4"));
            estimates.Setup(value => value.GetAt(4)).Returns(CreateEstimate("120051", "TXN-5"));
            IMsgSetResponse responseSet = CreateResponseSet(
                CreateResponse(ENResponseType.rtEstimateQueryRs, 0, "Status OK", estimates.Object));

            QBStatusResponse<List<QBEstimateReference>> result = request.Convert(responseSet);

            Assert.AreEqual(0, result.StatusCode);
            CollectionAssert.AreEqual(
                new[] { "120050", "120051" },
                result.Data.ConvertAll(value => value.Reference));
            CollectionAssert.AreEqual(
                new[] { "TXN-1", "TXN-5" },
                result.Data.ConvertAll(value => value.TransactionId));
        }

        [Test]
        public void QuoteIdentity_SalesOrderAddReturnsTransactionIdAndAssignedReference()
        {
            var request = new TestableSalesOrderRequest(CreateOrder("Q-300"));
            var salesOrder = new Mock<ISalesOrderRet>();
            salesOrder.SetupGet(value => value.TxnID).Returns(CreateId("TXN-SO-1").Object);
            salesOrder.SetupGet(value => value.RefNumber).Returns(CreateString("SO-9001").Object);
            IMsgSetResponse responseSet = CreateResponseSet(
                CreateResponse(ENResponseType.rtSalesOrderAddRs, 0, "Status OK", salesOrder.Object));

            QBStatusResponse<QBTransactionIdentity> result = request.Convert(responseSet);

            Assert.AreEqual("TXN-SO-1", result.Data.TransactionId);
            Assert.AreEqual("SO-9001", result.Data.AssignedReference);
        }

        [Test]
        public void QuoteIdentity_EstimateAddReturnsTransactionIdAndAssignedReference()
        {
            var request = new TestableEstimateRequest(CreateOrder("Q-400"));
            var estimate = new Mock<IEstimateRet>();
            estimate.SetupGet(value => value.TxnID).Returns(CreateId("TXN-EST-1").Object);
            estimate.SetupGet(value => value.RefNumber).Returns(CreateString("EST-400").Object);
            IMsgSetResponse responseSet = CreateResponseSet(
                CreateResponse(ENResponseType.rtEstimateAddRs, 0, "Status OK", estimate.Object));

            QBStatusResponse<QBTransactionIdentity> result = request.Convert(responseSet);

            Assert.AreEqual("TXN-EST-1", result.Data.TransactionId);
            Assert.AreEqual("EST-400", result.Data.AssignedReference);
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
            var result = ParseCache<QBStatusResponse<QBCommercialTermsCatalog>>("CommercialTermsQueryRequest",
                "<TermsQueryRs statusCode='0'><StandardTermsRet><Name>Net 30</Name></StandardTermsRet><DateDrivenTermsRet><Name>Due 15th</Name></DateDrivenTermsRet></TermsQueryRs><ShipMethodQueryRs statusCode='0'><ShipMethodRet><Name>UPS Ground</Name></ShipMethodRet></ShipMethodQueryRs>");
            Assert.AreEqual(0, result.StatusCode);
            CollectionAssert.AreEqual(new[] { "Net 30", "Due 15th" }, result.Data.CreditTerms);
            CollectionAssert.AreEqual(new[] { "UPS Ground" }, result.Data.ShippingMethods);
        }
        [Test]
        public void CustomerCommercialTermsQueryRequest_ReturnsAllAccountTermsForCache()
        {
            var result = ParseCache<QBStatusResponse<List<QBCustomerCommercialTermsRecord>>>("CustomerCommercialTermsQueryRequest",
                "<CustomerQueryRs statusCode='0'><CustomerRet><AccountNumber> EX-1042 </AccountNumber><TermsRef><FullName>Net 30</FullName></TermsRef><DataExtRet><DataExtName>Ship Account #</DataExtName><DataExtValue>UPS 12345</DataExtValue></DataExtRet></CustomerRet><CustomerRet><AccountNumber> </AccountNumber></CustomerRet></CustomerQueryRs>");
            Assert.AreEqual(0, result.StatusCode);
            Assert.AreEqual(1, result.Data.Count);
            Assert.AreEqual("EX-1042", result.Data[0].AccountNumber);
            Assert.AreEqual("Net 30", result.Data[0].CreditTerms);
            Assert.AreEqual("UPS 12345", result.Data[0].ShippingTerms);
        }

        private static T ParseCache<T>(string requestName, string responses)
        {
            Type type = typeof(RequestFactory).Assembly.GetType("QBRequestLibrary." + requestName);
            object request = FormatterServices.GetUninitializedObject(type);
            var response = new Mock<IMsgSetResponse>(MockBehavior.Strict);
            response.Setup(value => value.ToXMLString()).Returns("<QBXML><QBXMLMsgsRs>" + responses + "</QBXMLMsgsRs></QBXML>");
            return (T)type.GetMethod("ConvertResponse", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(request, new object[] { response.Object });
        }

        [Test]
        public void CacheXml_ItemsPreserveDescriptionsActivityAndEmptyFields()
        {
            var result = ParseCache<QBStatusResponse<List<QBItem>>>("AllItemNonInvQueryRequest",
                "<ItemNonInventoryQueryRs statusCode='0'><ItemNonInventoryRet><Name>A&amp;B</Name><IsActive>true</IsActive><SalesOrPurchase><Desc>First &lt;part&gt;</Desc></SalesOrPurchase></ItemNonInventoryRet><ItemNonInventoryRet><Name>Inactive</Name><IsActive>false</IsActive><SalesAndPurchase><SalesDesc>Sales description</SalesDesc></SalesAndPurchase></ItemNonInventoryRet></ItemNonInventoryQueryRs><ItemServiceQueryRs statusCode='0'><ItemServiceRet><Name>Service</Name></ItemServiceRet></ItemServiceQueryRs>");
            Assert.AreEqual(3, result.Data.Count);
            Assert.AreEqual("A&B", result.Data[0].Number);
            Assert.AreEqual("First <part>", result.Data[0].Description);
            Assert.IsTrue(result.Data[0].Active);
            Assert.AreEqual("Sales description", result.Data[1].Description);
            Assert.IsFalse(result.Data[1].Active);
            Assert.AreEqual(string.Empty, result.Data[2].Description);
            Assert.IsFalse(result.Data[2].Active);
        }

        [TestCase("<ItemNonInventoryQueryRs statusCode='0'/>")]
        [TestCase("<ItemNonInventoryQueryRs statusCode='0'/><ItemNonInventoryQueryRs statusCode='0'/>")]
        [TestCase("<ItemNonInventoryQueryRs/><ItemServiceQueryRs statusCode='0'/>")]
        [TestCase("<ItemNonInventoryQueryRs statusCode='garbage'/><ItemServiceQueryRs statusCode='0'/>")]
        public void CacheXml_RejectsIncompleteOrMalformedStatus(string responses)
        {
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                ParseCache<QBStatusResponse<List<QBItem>>>("AllItemNonInvQueryRequest", responses));
            Assert.IsInstanceOf<InvalidResponseException>(error.InnerException);
        }

        [Test]
        public void CacheXml_EmptyListsAreSuccessfulAndErrorsNeverPublishPartialData()
        {
            var empty = ParseCache<QBStatusResponse<List<QBItem>>>("AllItemNonInvQueryRequest",
                "<ItemNonInventoryQueryRs statusCode='1'/><ItemServiceQueryRs statusCode='1'/>");
            Assert.AreEqual(0, empty.StatusCode);
            Assert.IsEmpty(empty.Data);
            var failed = ParseCache<QBStatusResponse<List<QBItem>>>("AllItemNonInvQueryRequest",
                "<ItemNonInventoryQueryRs statusCode='3250'/><ItemServiceQueryRs statusCode='0'><ItemServiceRet><Name>Partial</Name></ItemServiceRet></ItemServiceQueryRs>");
            Assert.AreEqual(3250, failed.StatusCode);
            Assert.IsNull(failed.Data);
        }

        [Test]
        public void CacheXml_CustomersPreserveMissingTermsAndFirstMatchingCustomField()
        {
            var result = ParseCache<QBStatusResponse<List<QBCustomerCommercialTermsRecord>>>("CustomerCommercialTermsQueryRequest",
                "<CustomerQueryRs statusCode='0'><CustomerRet><AccountNumber>A</AccountNumber><DataExtRet><DataExtName>Unrelated</DataExtName><DataExtValue>Ignore</DataExtValue></DataExtRet><DataExtRet><DataExtName>ship account #</DataExtName><DataExtValue>First</DataExtValue></DataExtRet><DataExtRet><DataExtName>Ship Account #</DataExtName><DataExtValue>Second</DataExtValue></DataExtRet></CustomerRet><CustomerRet><AccountNumber>B</AccountNumber></CustomerRet></CustomerQueryRs>");
            Assert.AreEqual("First", result.Data[0].ShippingTerms);
            Assert.IsNull(result.Data[0].CreditTerms);
            Assert.IsNull(result.Data[1].ShippingTerms);
            Assert.IsNull(result.Data[1].CreditTerms);
        }

        [Test]
        public void Connection_CloseIsSafeBeforeOpenAndAfterFailedBeginSession()
        {
            var manager = new Mock<IQBSessionManager>();
            manager.Setup(value => value.BeginSession("", ENOpenMode.omDontCare))
                .Throws(new InvalidOperationException("Synthetic failure"));
            var connection = new TestableConnection(manager.Object);
            Assert.DoesNotThrow(() => connection.Close());
            Assert.Throws<Connection.ConnectionException>(() => connection.Open());
            Assert.DoesNotThrow(() => connection.Dispose());
            manager.Verify(value => value.EndSession(), Times.Never);
            manager.Verify(value => value.CloseConnection(), Times.Once);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CacheSession_ExcludesWatcherUntilNativeCloseFinishes(bool closeFails)
        {
            var manager = new Mock<IQBSessionManager>();
            manager.Setup(value => value.GetCurrentCompanyFileName()).Returns(@"C:\Synthetic\Example.qbw");
            manager.Setup(value => value.CloseConnection()).Callback(() =>
            {
                AssertGateOccupied();
                if (closeFails) throw new InvalidOperationException("Synthetic close failure");
            });
            var session = new CacheRefreshSession(new TestableConnection(manager.Object));
            try
            {
                AssertGateOccupied();
            }
            finally
            {
                if (closeFails) Assert.Throws<InvalidOperationException>(() => session.Dispose());
                else session.Dispose();
            }
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            using (QuickBooksSessionGate.Enter(timeout.Token)) { }
        }

        private static void AssertGateOccupied()
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                Assert.Throws<OperationCanceledException>(() =>
                {
                    using (QuickBooksSessionGate.Enter(timeout.Token)) { }
                });
        }

        [Test]
        public void CacheSession_ReadsUseOneConnectionAndCloseExactlyOnce()
        {
            var manager = new Mock<IQBSessionManager>();
            manager.Setup(value => value.GetCurrentCompanyFileName()).Returns(@"C:\Synthetic\Test.qbw");
            var connection = new TestableConnection(manager.Object);
            using (var session = new CacheRefreshSession(connection))
            {
                foreach (object request in new object[] { session.Items, session.Terms, session.Customers })
                    Assert.AreSame(connection, request.GetType().GetField("_connection",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(request));
                var read = new SharedReadProbe();
                typeof(Request<object, int>).GetMethod("UseOpenConnection",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(read, new object[] { connection });
                Assert.AreEqual(7, read.SendRequest());
                read.Fail = true;
                Assert.Throws<InvalidOperationException>(() => read.SendRequest());
                manager.Verify(value => value.EndSession(), Times.Never);
            }
            connection.Close();
            manager.Verify(value => value.OpenConnection("", "Proto-CAM QB Library"), Times.Once);
            manager.Verify(value => value.BeginSession("", ENOpenMode.omDontCare), Times.Once);
            manager.Verify(value => value.EndSession(), Times.Once);
            manager.Verify(value => value.CloseConnection(), Times.Once);
        }

        private sealed class SharedReadProbe : Request<object, int>
        {
            public bool Fail;
            protected override void BuildHelper() { }
            protected override int ConvertResponse(IMsgSetResponse response) => 7;
            protected override int Send()
            {
                if (Fail) throw new InvalidOperationException("Synthetic read failure");
                return 7;
            }
        }
        [Test]
        public void CustomerCommercialTermsQueryRequest_RequestsPublicCustomerCustomFields()
        {
            var request = new TestableCustomerCommercialTermsQueryRequest();
            var msgSetRequest = new Mock<IMsgSetRequest>();
            var customerQuery = new Mock<ICustomerQuery> { DefaultValue = DefaultValue.Mock };
            msgSetRequest.Setup(value => value.AppendCustomerQueryRq())
                .Returns(customerQuery.Object);

            request.BuildInto(msgSetRequest.Object);

            Mock.Get(customerQuery.Object.IncludeRetElementList)
                .Verify(value => value.Add("DataExtRet"), Times.Once);
            Mock.Get(customerQuery.Object.OwnerIDList)
                .Verify(value => value.Add("0"), Times.Once);
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

        private static IEstimateRet CreateEstimate(string reference, string transactionId)
        {
            var estimate = new Mock<IEstimateRet>();
            estimate.SetupGet(value => value.RefNumber).Returns(CreateString(reference).Object);
            estimate.SetupGet(value => value.TxnID).Returns(CreateId(transactionId).Object);
            return estimate.Object;
        }

        private static Mock<IQBIDType> CreateId(string value)
        {
            var result = new Mock<IQBIDType>();
            result.Setup(field => field.GetValue()).Returns(value);
            return result;
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

            public TestableSalesOrderRequest(
                QBOrder salesOrder,
                string approvedCompanyFingerprint,
                Connection connection)
                : base(salesOrder, approvedCompanyFingerprint)
            {
                GC.SuppressFinalize(_connection);
                _connection = connection;
                GC.SuppressFinalize(this);
            }

            public void BuildInto(IMsgSetRequest msgSetRequest)
            {
                _msgSetRequest = msgSetRequest;
                BuildHelper();
            }

            public QBStatusResponse<QBTransactionIdentity> Convert(IMsgSetResponse responseSet)
            {
                return ConvertResponse(responseSet);
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

            public TestableEstimateRequest(
                QBOrder estimate,
                string approvedCompanyFingerprint,
                Connection connection)
                : base(estimate, approvedCompanyFingerprint)
            {
                GC.SuppressFinalize(_connection);
                _connection = connection;
                GC.SuppressFinalize(this);
            }

            public void BuildInto(IMsgSetRequest msgSetRequest)
            {
                _msgSetRequest = msgSetRequest;
                BuildHelper();
            }

            public QBStatusResponse<QBTransactionIdentity> Convert(IMsgSetResponse responseSet)
            {
                return ConvertResponse(responseSet);
            }
        }

        private sealed class TestableAddItemNonInventoryRequest
            : AddItemNonInventoryRequest
        {
            public TestableAddItemNonInventoryRequest(
                List<QBItem> items,
                string approvedCompanyFingerprint,
                Connection connection)
                : base(items, approvedCompanyFingerprint)
            {
                GC.SuppressFinalize(_connection);
                _connection = connection;
                GC.SuppressFinalize(this);
            }
        }

        private sealed class TestableEstimateReferenceQueryRequest
            : EstimateReferenceQueryRequest
        {
            public TestableEstimateReferenceQueryRequest(string reference)
                : base(reference)
            {
                GC.SuppressFinalize(this);
                GC.SuppressFinalize(_connection);
            }

            public TestableEstimateReferenceQueryRequest(
                string reference,
                string approvedCompanyFingerprint,
                Connection connection)
                : base(reference, approvedCompanyFingerprint)
            {
                GC.SuppressFinalize(_connection);
                _connection = connection;
                GC.SuppressFinalize(this);
            }

            public void BuildInto(IMsgSetRequest msgSetRequest)
            {
                _msgSetRequest = msgSetRequest;
                BuildHelper();
            }

            public QBStatusResponse<List<QBEstimateReference>> Convert(IMsgSetResponse responseSet)
            {
                return ConvertResponse(responseSet);
            }
        }

        private sealed class FailingEstimateReferenceQueryRequest
            : EstimateReferenceQueryRequest
        {
            private readonly string _message;

            public FailingEstimateReferenceQueryRequest(string message)
                : base("TEST-ABC234")
            {
                _message = message;
                GC.SuppressFinalize(this);
                GC.SuppressFinalize(_connection);
            }

            protected override void Connect(string file = "")
            {
                _open = true;
            }

            protected override void Disconnect()
            {
                _open = false;
            }

            protected override QBStatusResponse<List<QBEstimateReference>> Send()
            {
                throw new InvalidOperationException(_message);
            }
        }

        private sealed class TestableConnection : Connection
        {
            private readonly IQBSessionManager _sessionManager;

            public TestableConnection(IQBSessionManager sessionManager)
            {
                _sessionManager = sessionManager;
                GC.SuppressFinalize(this);
            }

            protected override IQBSessionManager CreateSessionManager()
            {
                return _sessionManager;
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

        private sealed class TestableCustomerCommercialTermsQueryRequest
            : CustomerCommercialTermsQueryRequest
        {
            public TestableCustomerCommercialTermsQueryRequest()
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
