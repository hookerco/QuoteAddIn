// QuickBooksServiceLibrary.Tests/QuickBooksServiceTests.cs
using NUnit.Framework;
using Moq;
using QBRequestLibrary;
using QuickBooksConnectorCore;
using QuickBooksIPCContracts;
using QuickBooksIPCService;
using System;
using System.Collections.Generic;
using System.IO;

namespace QuickBooksServiceLibrary.Tests
{
    [TestFixture]
    public class QuickBooksServiceTests
    {
        private Mock<IRequestFactory> _mockRequestFactory;
        private QuickBooksService _service;
        private string _logPath;

        [SetUp]
        public void Setup()
        {
            _mockRequestFactory = new Mock<IRequestFactory>();
            _logPath = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "logs",
                Guid.NewGuid().ToString("N") + ".log");
            _service = new QuickBooksService(_mockRequestFactory.Object, new Logger(_logPath), initialize: false);
        }

        [Test]
        public void AddOrder_ShouldCreateSalesOrderRequestAndSend()
        {
            // Arrange
            var order = new QBOrder
            {
                Customer = new QBCustomer { Name = "CustomerName", PO = "PO123" },
                DueDate = DateTime.Now,
                Items = new List<QBItem>
                {
                    new QBItem { Number = "Item1", Quantity = 2, Rate = 10.5 },
                    new QBItem { Number = "Item2", Quantity = 1, Rate = 20 }
                }
            };

            var expectedResponse = new QBStatusResponse<QBTransactionIdentity>
            {
                StatusCode = 0,
                StatusMessage = "Order Added Successfully",
                Data = new QBTransactionIdentity
                {
                    TransactionId = "TXN-SO-1",
                    AssignedReference = "SO-9001"
                }
            };

            var mockSalesOrderRequest = new Mock<ISalesOrderRequest>();
            mockSalesOrderRequest.Setup(r => r.SendRequest()).Returns(expectedResponse);

            _mockRequestFactory
                .Setup(f => f.CreateSalesOrderRequest(order, null))
                .Returns(mockSalesOrderRequest.Object);

            // Act
            var result = _service.AddOrder(order);

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.AreEqual(expectedResponse.StatusCode, result.StatusCode);
            Assert.AreEqual(expectedResponse.StatusMessage, result.StatusMessage);
            Assert.AreEqual("TXN-SO-1", result.Data);

            _mockRequestFactory.Verify(f => f.CreateSalesOrderRequest(order, null), Times.Once);
            mockSalesOrderRequest.Verify(r => r.SendRequest(), Times.Once);
        }

        [Test]
        public void QuoteIdentity_GetEstimateReferencesUsesUnfilteredQuery()
        {
            var expectedResponse = new QBStatusResponse<List<QBEstimateReference>>
            {
                StatusCode = 0,
                StatusMessage = "OK",
                Data = new List<QBEstimateReference>
                {
                    new QBEstimateReference { Reference = "120050", TransactionId = "TXN-1" }
                }
            };
            var query = new Mock<IEstimateReferenceQueryRequest>();
            query.Setup(value => value.SendRequest()).Returns(expectedResponse);
            _mockRequestFactory.Setup(value => value.CreateEstimateReferenceQueryRequest(null))
                .Returns(query.Object);

            QBStatusResponse<List<QBEstimateReference>> result = _service.GetEstimateReferences();

            Assert.AreSame(expectedResponse, result);
            _mockRequestFactory.Verify(
                value => value.CreateEstimateReferenceQueryRequest(null), Times.Once);
        }

        [Test]
        public void QuoteNumberAdmin_GetCurrentCompanyFingerprintReturnsOnlyFingerprint()
        {
            const string companyFile = @"C:\Synthetic\Test Company.qbw";
            _service = new QuickBooksService(
                _mockRequestFactory.Object,
                new Logger(_logPath),
                () => companyFile,
                initialize: false);

            QBStatusResponse<string> result = _service.GetCurrentCompanyFingerprint();

            Assert.AreEqual(0, result.StatusCode);
            Assert.AreEqual("OK", result.StatusMessage);
            Assert.AreEqual(
                QuickBooksService.FingerprintCompanyFileName(companyFile),
                result.Data);
            StringAssert.DoesNotContain("Test Company.qbw", result.Data);
        }

        [Test]
        public void QuoteNumberAdmin_GetCurrentCompanyFingerprintFailureIsGeneric()
        {
            _service = new QuickBooksService(
                _mockRequestFactory.Object,
                new Logger(_logPath),
                () => throw new InvalidOperationException(
                    @"invented C:\Sensitive\Company.qbw customer Acme QBFC detail"),
                initialize: false);

            QBStatusResponse<string> result = _service.GetCurrentCompanyFingerprint();

            Assert.AreNotEqual(0, result.StatusCode);
            Assert.AreEqual("QuickBooks company identity is unavailable.", result.StatusMessage);
            Assert.IsNull(result.Data);
            StringAssert.DoesNotContain("Company.qbw", result.StatusMessage);
            StringAssert.DoesNotContain("customer Acme", result.StatusMessage);
            StringAssert.DoesNotContain("QBFC detail", result.StatusMessage);
        }

        [Test]
        public void QuoteIdentity_GetEstimateReferenceUsesExactQueryAndReturnsFirstMatch()
        {
            var queryResponse = new QBStatusResponse<List<QBEstimateReference>>
            {
                StatusCode = 0,
                StatusMessage = "OK",
                Data = new List<QBEstimateReference>
                {
                    new QBEstimateReference { Reference = "TEST-ABC123", TransactionId = "TXN-2" }
                }
            };
            var query = new Mock<IEstimateReferenceQueryRequest>();
            query.Setup(value => value.SendRequest()).Returns(queryResponse);
            _mockRequestFactory
                .Setup(value => value.CreateEstimateReferenceQueryRequest("TEST-ABC123"))
                .Returns(query.Object);

            QBStatusResponse<QBEstimateReference> result =
                _service.GetEstimateReference("TEST-ABC123");

            Assert.AreEqual(0, result.StatusCode);
            Assert.AreEqual("TEST-ABC123", result.Data.Reference);
            Assert.AreEqual("TXN-2", result.Data.TransactionId);
        }

        [Test]
        public void GetCustomer_ShouldCreateCustomerQueryRequestAndSend()
        {
            // Arrange
            string accountNumber = "12345";
            var expectedCustomer = new QBCustomer
            {
                AccountNumber = accountNumber,
                Name = "Test Customer"
            };

            var mockCustomerQueryRequest = new Mock<ICustomerQueryRequest>();
            mockCustomerQueryRequest.Setup(r => r.SendRequest()).Returns(expectedCustomer);

            _mockRequestFactory
                .Setup(f => f.CreateCustomerQueryRequest(accountNumber))
                .Returns(mockCustomerQueryRequest.Object);

            // Act
            var result = _service.GetCustomer(accountNumber);

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.AreEqual(expectedCustomer.AccountNumber, result.AccountNumber);
            Assert.AreEqual(expectedCustomer.Name, result.Name);

            _mockRequestFactory.Verify(f => f.CreateCustomerQueryRequest(accountNumber), Times.Once);
            mockCustomerQueryRequest.Verify(r => r.SendRequest(), Times.Once);
        }

        [Test]
        public void GetAllItems_ShouldCreateAllItemNonInvQueryRequestAndSend()
        {
            // Arrange
            var expectedResponse = new QBStatusResponse<List<QBItem>>
            {
                StatusCode = 0,
                StatusMessage = "Items Retrieved Successfully",
                Data = new List<QBItem>
                {
                    // Could be non-inventory or service items; the service aggregates both.
                    new QBItem { Number = "Item1", Description = "Description1", Active = true },
                    new QBItem { Number = "Item2", Description = "Description2", Active = false }
                }
            };

            var mockAllItemNonInvQueryRequest = new Mock<IAllItemNonInvQueryRequest>();
            mockAllItemNonInvQueryRequest.Setup(r => r.SendRequest()).Returns(expectedResponse);

            _mockRequestFactory
                .Setup(f => f.CreateAllItemNonInvQueryRequest())
                .Returns(mockAllItemNonInvQueryRequest.Object);

            // Act
            var result = _service.GetAllItems();

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.AreEqual(expectedResponse.StatusCode, result.StatusCode);
            Assert.AreEqual(expectedResponse.StatusMessage, result.StatusMessage);
            Assert.AreEqual(2, result.Data.Count);
            Assert.IsTrue(result.Data[0].Number == "Item1");
            Assert.IsTrue(result.Data[0].Description == "Description1");
            Assert.IsTrue(result.Data[1].Number == "Item2");
            Assert.IsTrue(result.Data[1].Description == "Description2");
            Assert.IsTrue(result.Data[0].Active == true);
            Assert.IsTrue(result.Data[1].Active == false);

            _mockRequestFactory.Verify(f => f.CreateAllItemNonInvQueryRequest(), Times.AtLeastOnce);
            mockAllItemNonInvQueryRequest.Verify(r => r.SendRequest(), Times.AtLeastOnce);
        }

        [Test]
        public void AddNonInvItem_ShouldCreateAddItemNonInventoryRequestAndSend()
        {
            // Arrange
            var items = new List<QBItem>
            {
                new QBItem { Number = "Item1", Description = "Description1" },
                new QBItem { Number = "Item2", Description = "Description2" }
            };

            var expectedResponses = new List<QBStatusResponse<string>>
            {
                new QBStatusResponse<string> { StatusCode = 0, StatusMessage = "Item1 Added" },
                new QBStatusResponse<string> { StatusCode = 0, StatusMessage = "Item2 Added" }
            };

            var mockAddItemNonInventoryRequest = new Mock<IAddItemNonInventoryRequest>();
            mockAddItemNonInventoryRequest.Setup(r => r.SendRequest()).Returns(expectedResponses);

            _mockRequestFactory
                .Setup(f => f.CreateAddItemNonInventoryRequest(items, null))
                .Returns(mockAddItemNonInventoryRequest.Object);

            // Act
            var result = _service.AddNonInvItem(items);

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.AreEqual(2, result.Count);
            Assert.AreEqual(0, result[0].StatusCode);
            Assert.AreEqual("Item1 Added", result[0].StatusMessage);
            Assert.AreEqual(0, result[1].StatusCode);
            Assert.AreEqual("Item2 Added", result[1].StatusMessage);

            _mockRequestFactory.Verify(
                f => f.CreateAddItemNonInventoryRequest(items, null),
                Times.Once);
            mockAddItemNonInventoryRequest.Verify(r => r.SendRequest(), Times.Once);
        }

        [Test]
        public void SubmitQuote_WithExistingItemCreatesSalesOrderAndReturnsResolvedLines()
        {
            _service.InvalidateAllItemsCache();
            var request = new QBQuoteUploadRequest
            {
                QuoteKind = "normal",
                TransactionType = QBQuoteTransactionType.SalesOrder,
                QuoteNumber = "120100",
                Customer = new QBCustomer { Name = "CustomerName" },
                CustomerPO = "PO-100",
                DueDate = new DateTime(2026, 6, 16),
                Lines = new List<QBQuoteUploadLine>
                {
                    new QBQuoteUploadLine
                    {
                        Description = "RB-2500A-03000, Radius Block",
                        Quantity = 2,
                        Rate = 12.5
                    }
                }
            };

            var itemResponse = new QBStatusResponse<List<QBItem>>
            {
                StatusCode = 0,
                StatusMessage = "OK",
                Data = new List<QBItem>
                {
                    new QBItem { Number = "1-1000", Description = "RB-2500A-03000, Radius Block", Active = true }
                }
            };
            var mockAllItemsRequest = new Mock<IAllItemNonInvQueryRequest>();
            mockAllItemsRequest.Setup(r => r.SendRequest()).Returns(itemResponse);
            _mockRequestFactory
                .Setup(f => f.CreateAllItemNonInvQueryRequest())
                .Returns(mockAllItemsRequest.Object);

            var orderResponse = new QBStatusResponse<QBTransactionIdentity>
            {
                StatusCode = 0,
                StatusMessage = "OK",
                Data = new QBTransactionIdentity
                {
                    TransactionId = "TXN-SO-1",
                    AssignedReference = "SO-9001"
                }
            };
            var mockSalesOrderRequest = new Mock<ISalesOrderRequest>();
            mockSalesOrderRequest.Setup(r => r.SendRequest()).Returns(orderResponse);
            _mockRequestFactory
                .Setup(f => f.CreateSalesOrderRequest(
                    It.Is<QBOrder>(order =>
                        order.QuoteNumber == "120100" &&
                        order.Customer.Name == "CustomerName" &&
                        order.Customer.PO == "PO-100" &&
                        order.DueDate == new DateTime(2026, 6, 16) &&
                        order.Items.Count == 1 &&
                        order.Items[0].Number == "1-1000" &&
                        order.Items[0].Description == "RB-2500A-03000, Radius Block" &&
                        order.Items[0].Quantity == 2 &&
                        order.Items[0].Rate == 12.5),
                    null))
                .Returns(mockSalesOrderRequest.Object);

            QBStatusResponse<QBQuoteUploadResult> result = _service.SubmitQuote(request);

            Assert.AreEqual(0, result.StatusCode);
            Assert.AreEqual("OK", result.StatusMessage);
            Assert.AreEqual(QBQuoteTransactionType.SalesOrder, result.Data.TransactionType);
            Assert.AreEqual("CustomerName", result.Data.CustomerName);
            Assert.AreEqual("120100", result.Data.QuoteNumber);
            Assert.AreEqual("TXN-SO-1", result.Data.TransactionId);
            Assert.AreEqual("SO-9001", result.Data.AssignedReference);
            Assert.AreEqual("1-1000", result.Data.Lines[0].Number);
            Assert.IsFalse(result.Data.Lines[0].CreatedItem);
            _mockRequestFactory.Verify(
                f => f.CreateSalesOrderRequest(It.IsAny<QBOrder>(), null),
                Times.Once);
            _mockRequestFactory.Verify(
                f => f.CreateAddItemNonInventoryRequest(It.IsAny<List<QBItem>>(), null),
                Times.Never);
        }

        [Test]
        public void SubmitQuote_WhenCustomerLookupFailsReturnsFailureWithoutCreatingTransaction()
        {
            var request = new QBQuoteUploadRequest
            {
                QuoteKind = "normal",
                TransactionType = QBQuoteTransactionType.Estimate,
                QuoteNumber = "120404",
                CustomerAccountNumber = "404",
                Lines = new List<QBQuoteUploadLine>
                {
                    new QBQuoteUploadLine { Description = "RB-2500A-03000, Radius Block", Quantity = 1, Rate = 1 }
                }
            };

            var mockCustomerRequest = new Mock<ICustomerAccountNumberQueryRequest>();
            mockCustomerRequest.Setup(r => r.SendRequest()).Returns((QBCustomer)null);
            _mockRequestFactory
                .Setup(f => f.CreateCustomerAccountNumberQueryRequest("404"))
                .Returns(mockCustomerRequest.Object);
            ArrangeEstimateQuery(new List<string>(), request.QuoteNumber, null);

            QBStatusResponse<QBQuoteUploadResult> result = _service.SubmitQuote(request);

            Assert.AreNotEqual(0, result.StatusCode);
            StringAssert.Contains("Customer not found", result.StatusMessage);
            Assert.IsNull(result.Data);
            _mockRequestFactory.Verify(
                f => f.CreateCustomerAccountNumberQueryRequest("404"), Times.Once);
            _mockRequestFactory.Verify(
                f => f.CreateCustomerQueryRequest(It.IsAny<string>()), Times.Never);
            _mockRequestFactory.Verify(
                f => f.CreateEstimateRequest(It.IsAny<QBOrder>(), It.IsAny<string>()),
                Times.Never);
            _mockRequestFactory.Verify(
                f => f.CreateSalesOrderRequest(It.IsAny<QBOrder>(), It.IsAny<string>()),
                Times.Never);
        }

        [Test]
        public void SubmitQuote_WithInvalidTransactionTypeReturnsFailureBeforeQuickBooksRequests()
        {
            var request = new QBQuoteUploadRequest
            {
                QuoteKind = "normal",
                TransactionType = (QBQuoteTransactionType)999,
                QuoteNumber = "120999",
                Customer = new QBCustomer { Name = "CustomerName" },
                DueDate = new DateTime(2026, 6, 16),
                Lines = new List<QBQuoteUploadLine>
                {
                    new QBQuoteUploadLine { Description = "RB-2500A-03000, Radius Block", Quantity = 1, Rate = 1 }
                }
            };

            QBStatusResponse<QBQuoteUploadResult> result = _service.SubmitQuote(request);

            Assert.AreNotEqual(0, result.StatusCode);
            StringAssert.Contains("TransactionType", result.StatusMessage);
            Assert.IsNull(result.Data);
            _mockRequestFactory.Verify(f => f.CreateAllItemNonInvQueryRequest(), Times.Never);
            _mockRequestFactory.Verify(
                f => f.CreateAddItemNonInventoryRequest(
                    It.IsAny<List<QBItem>>(),
                    It.IsAny<string>()),
                Times.Never);
            _mockRequestFactory.Verify(
                f => f.CreateEstimateRequest(It.IsAny<QBOrder>(), It.IsAny<string>()),
                Times.Never);
            _mockRequestFactory.Verify(
                f => f.CreateSalesOrderRequest(It.IsAny<QBOrder>(), It.IsAny<string>()),
                Times.Never);
        }

        [Test]
        public void SubmitQuote_SalesOrderWithMinDueDateReturnsFailureBeforeQuickBooksRequests()
        {
            var request = new QBQuoteUploadRequest
            {
                QuoteKind = "normal",
                TransactionType = QBQuoteTransactionType.SalesOrder,
                QuoteNumber = "120998",
                Customer = new QBCustomer { Name = "CustomerName" },
                DueDate = DateTime.MinValue,
                Lines = new List<QBQuoteUploadLine>
                {
                    new QBQuoteUploadLine { Description = "RB-2500A-03000, Radius Block", Quantity = 1, Rate = 1 }
                }
            };

            QBStatusResponse<QBQuoteUploadResult> result = _service.SubmitQuote(request);

            Assert.AreNotEqual(0, result.StatusCode);
            StringAssert.Contains("DueDate", result.StatusMessage);
            Assert.IsNull(result.Data);
            _mockRequestFactory.Verify(f => f.CreateAllItemNonInvQueryRequest(), Times.Never);
            _mockRequestFactory.Verify(
                f => f.CreateAddItemNonInventoryRequest(
                    It.IsAny<List<QBItem>>(),
                    It.IsAny<string>()),
                Times.Never);
            _mockRequestFactory.Verify(
                f => f.CreateEstimateRequest(It.IsAny<QBOrder>(), It.IsAny<string>()),
                Times.Never);
            _mockRequestFactory.Verify(
                f => f.CreateSalesOrderRequest(It.IsAny<QBOrder>(), It.IsAny<string>()),
                Times.Never);
        }

        [Test]
        public void QuoteWriteSafety_ConnectorParsesPolicyFields()
        {
            const string payload =
                "{\"quote_kind\":\"test\",\"confirmed_transaction_id\":\"TXN-CONFIRMED\"," +
                "\"approved_test_company_fingerprints\":[\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"]," +
                "\"transaction_type\":\"Estimate\",\"quote_number\":\"TEST-ABC234\"," +
                "\"customer\":{\"name\":\"Synthetic Customer\"}," +
                "\"lines\":[{\"description\":\"Synthetic item\",\"quantity\":1,\"rate\":10}]}";

            QBQuoteUploadRequest request = SubmitQuoteHandler.ParseQuoteUploadRequest(payload);

            Assert.AreEqual("test", request.QuoteKind);
            Assert.AreEqual("TXN-CONFIRMED", request.ConfirmedTransactionId);
            CollectionAssert.AreEqual(
                new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
                request.ApprovedTestCompanyFingerprints);
        }

        [Test]
        public void QuoteWriteSafety_FingerprintNormalizesWindowsPathBeforeSha256()
        {
            string fingerprint = QuickBooksService.FingerprintCompanyFileName(
                @"c:/Synthetic/Folder/../Test Company.qbw/");

            Assert.AreEqual(
                "828d8bf48c8f330e5976950dc79fc367dd15f06152df0af66ad8341ba185fc4a",
                fingerprint);
        }

        [TestCase(QBQuoteTransactionType.Estimate)]
        [TestCase(QBQuoteTransactionType.SalesOrder)]
        public void QuoteWriteSafety_TestCompanyMismatchStopsBeforeEveryLookupAndWrite(
            QBQuoteTransactionType transactionType)
        {
            var calls = new List<string>();
            QuickBooksService service = ServiceWithCompany(
                () =>
                {
                    calls.Add("company");
                    return @"C:\Synthetic\Current Company.qbw";
                });
            QBQuoteUploadRequest request = QuoteRequest(transactionType, "test");
            request.ApprovedTestCompanyFingerprints = new List<string>
            {
                QuickBooksService.FingerprintCompanyFileName(@"C:\Synthetic\Approved Company.qbw")
            };

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(1, result.StatusCode);
            Assert.AreEqual(
                "Test quote cannot be submitted to this QuickBooks company.",
                result.StatusMessage);
            CollectionAssert.AreEqual(new[] { "company" }, calls);
            AssertNoQuoteLookupsOrWrites();
        }

        [TestCase(QBQuoteTransactionType.Estimate, null)]
        [TestCase(QBQuoteTransactionType.Estimate, "not-a-fingerprint")]
        [TestCase(QBQuoteTransactionType.SalesOrder, null)]
        [TestCase(QBQuoteTransactionType.SalesOrder, "not-a-fingerprint")]
        public void QuoteWriteSafety_TestAllowlistFailsClosedBeforeEveryLookupAndWrite(
            QBQuoteTransactionType transactionType,
            string allowlistValue)
        {
            var calls = new List<string>();
            QuickBooksService service = ServiceWithCompany(
                () =>
                {
                    calls.Add("company");
                    return @"C:\Synthetic\Current Company.qbw";
                });
            QBQuoteUploadRequest request = QuoteRequest(transactionType, "test");
            request.ApprovedTestCompanyFingerprints = allowlistValue == null
                ? new List<string>()
                : new List<string> { allowlistValue };

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(1, result.StatusCode);
            Assert.AreEqual(
                "Test quote cannot be submitted to this QuickBooks company.",
                result.StatusMessage);
            CollectionAssert.AreEqual(new[] { "company" }, calls);
            AssertNoQuoteLookupsOrWrites();
        }

        [TestCase(QBQuoteTransactionType.Estimate)]
        [TestCase(QBQuoteTransactionType.SalesOrder)]
        public void QuoteWriteSafety_UnidentifiableTestCompanyReturnsGenericFailureWithoutLeakingDetails(
            QBQuoteTransactionType transactionType)
        {
            const string privateDetail = @"C:\Synthetic\Private\Company.qbw QBFC detail";
            var calls = new List<string>();
            QuickBooksService service = ServiceWithCompany(
                () =>
                {
                    calls.Add("company");
                    throw new InvalidOperationException(privateDetail);
                });
            QBQuoteUploadRequest request = QuoteRequest(transactionType, "test");
            request.ApprovedTestCompanyFingerprints = new List<string>
            {
                QuickBooksService.FingerprintCompanyFileName(@"C:\Synthetic\Approved Company.qbw")
            };

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(
                "Test quote cannot be submitted to this QuickBooks company.",
                result.StatusMessage);
            StringAssert.DoesNotContain(privateDetail, result.StatusMessage);
            CollectionAssert.AreEqual(new[] { "company" }, calls);
            Assert.IsTrue(File.Exists(_logPath));
            StringAssert.DoesNotContain(privateDetail, File.ReadAllText(_logPath));
            AssertNoQuoteLookupsOrWrites();
        }

        [Test]
        public void QuoteWriteSafety_AllowedTestEstimateRunsCompanyThenExactQueryThenWritePipeline()
        {
            var calls = new List<string>();
            const string companyPath = @"C:\Synthetic\Approved Company.qbw";
            QuickBooksService service = ServiceWithCompany(
                () =>
                {
                    calls.Add("company");
                    return companyPath;
                });
            QBQuoteUploadRequest request = QuoteRequest(QBQuoteTransactionType.Estimate, "test");
            request.ApprovedTestCompanyFingerprints = new List<string>
            {
                QuickBooksService.FingerprintCompanyFileName(companyPath)
            };
            string writeFingerprint = request.ApprovedTestCompanyFingerprints[0];
            ArrangeEstimateQuery(calls, request.QuoteNumber, null, writeFingerprint);
            ArrangeCustomerAndItems(calls, request);
            ArrangeEstimateAdd(calls, "TXN-EST-1", request.QuoteNumber, writeFingerprint);

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(0, result.StatusCode);
            Assert.AreEqual("TXN-EST-1", result.Data.TransactionId);
            Assert.AreEqual(request.QuoteNumber, result.Data.AssignedReference);
            CollectionAssert.AreEqual(
                new[] { "company", "estimate-query", "customer", "items", "estimate-add" },
                calls);
        }

        [Test]
        public void QuoteWriteSafety_TestEstimateExactQueryIsBoundAcrossAtoBtoARace()
        {
            var calls = new List<string>();
            const string companyA = @"C:\Synthetic\Approved Company A.qbw";
            const string companyB = @"C:\Synthetic\Other Company B.qbw";
            string currentCompany = companyA;
            QuickBooksService service = ServiceWithCompany(
                () =>
                {
                    calls.Add("preflight-A");
                    return currentCompany;
                });
            QBQuoteUploadRequest request = QuoteRequest(QBQuoteTransactionType.Estimate, "test");
            string approvedFingerprint =
                QuickBooksService.FingerprintCompanyFileName(companyA);
            request.ApprovedTestCompanyFingerprints = new List<string>
            {
                approvedFingerprint
            };

            var query = new Mock<IEstimateReferenceQueryRequest>();
            query.Setup(value => value.SendRequest())
                .Callback(() =>
                {
                    currentCompany = companyB;
                    calls.Add("query-session-B");
                    currentCompany = companyA;
                })
                .Throws(new QBRequestLibraryRuntimeError(
                    "QuickBooks company verification failed."));
            _mockRequestFactory.Setup(value => value.CreateEstimateReferenceQueryRequest(
                    request.QuoteNumber,
                    approvedFingerprint))
                .Returns(query.Object);

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(1, result.StatusCode);
            Assert.AreEqual(
                "QuickBooks Estimate preflight is unavailable.",
                result.StatusMessage);
            CollectionAssert.AreEqual(new[] { "preflight-A", "query-session-B" }, calls);
            _mockRequestFactory.Verify(value => value.CreateEstimateReferenceQueryRequest(
                    request.QuoteNumber,
                    approvedFingerprint),
                Times.Once);
            AssertNoCustomerItemOrTransactionWrites();
        }

        [Test]
        public void QuoteWriteSafety_AllowedTestSalesOrderSkipsEstimateQueryAndWritesAfterCompanyGate()
        {
            var calls = new List<string>();
            const string companyPath = @"C:\Synthetic\Approved Company.qbw";
            QuickBooksService service = ServiceWithCompany(
                () =>
                {
                    calls.Add("company");
                    return companyPath;
                });
            QBQuoteUploadRequest request = QuoteRequest(QBQuoteTransactionType.SalesOrder, "test");
            request.ApprovedTestCompanyFingerprints = new List<string>
            {
                QuickBooksService.FingerprintCompanyFileName(companyPath)
            };
            string writeFingerprint = request.ApprovedTestCompanyFingerprints[0];
            ArrangeCustomerAndItems(calls, request);
            ArrangeSalesOrderAdd(calls, "TXN-SO-TEST", "SO-TEST-1", writeFingerprint);

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(0, result.StatusCode);
            Assert.AreEqual("TXN-SO-TEST", result.Data.TransactionId);
            Assert.AreEqual("SO-TEST-1", result.Data.AssignedReference);
            CollectionAssert.AreEqual(
                new[] { "company", "customer", "items", "sales-order-add" },
                calls);
            _mockRequestFactory.Verify(
                value => value.CreateEstimateReferenceQueryRequest(It.IsAny<string>()),
                Times.Never);
        }

        [Test]
        public void QuoteWriteSafety_NormalEstimateQueriesExactReferenceBeforeCustomerItemsAndWrite()
        {
            var calls = new List<string>();
            QuickBooksService service = ServiceWithCompany(
                () => throw new AssertionException("Normal quotes must not read the company file."));
            QBQuoteUploadRequest request = QuoteRequest(QBQuoteTransactionType.Estimate, "normal");
            ArrangeEstimateQuery(calls, request.QuoteNumber, null);
            ArrangeCustomerAndItems(calls, request);
            ArrangeEstimateAdd(calls, "TXN-EST-2", request.QuoteNumber, null);

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(0, result.StatusCode);
            CollectionAssert.AreEqual(
                new[] { "estimate-query", "customer", "items", "estimate-add" },
                calls);
        }

        [Test]
        public void QuoteWriteSafety_NormalSalesOrderRetainsWritePipelineWithoutCompanyOrEstimateQuery()
        {
            var calls = new List<string>();
            QuickBooksService service = ServiceWithCompany(
                () => throw new AssertionException("Normal quotes must not read the company file."));
            QBQuoteUploadRequest request = QuoteRequest(QBQuoteTransactionType.SalesOrder, "normal");
            ArrangeCustomerAndItems(calls, request);
            ArrangeSalesOrderAdd(calls, "TXN-SO-1", "SO-9001", null);

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(0, result.StatusCode);
            CollectionAssert.AreEqual(new[] { "customer", "items", "sales-order-add" }, calls);
            _mockRequestFactory.Verify(
                value => value.CreateEstimateReferenceQueryRequest(It.IsAny<string>()),
                Times.Never);
        }

        [Test]
        public void QuoteWriteSafety_TestFingerprintPropagatesToItemAndEstimateWriteRequestsInOrder()
        {
            var calls = new List<string>();
            const string companyPath = @"C:\Synthetic\Approved Company.qbw";
            string writeFingerprint = QuickBooksService.FingerprintCompanyFileName(companyPath);
            QuickBooksService service = ServiceWithCompany(
                () =>
                {
                    calls.Add("company");
                    return companyPath;
                });
            QBQuoteUploadRequest request = QuoteRequest(QBQuoteTransactionType.Estimate, "test");
            request.ApprovedTestCompanyFingerprints = new List<string> { writeFingerprint };
            request.Lines[0].OverrideNumber = "1-NEW";
            ArrangeEstimateQuery(calls, request.QuoteNumber, null, writeFingerprint);

            var customer = new Mock<ICustomerAccountNumberQueryRequest>();
            customer.Setup(value => value.SendRequest())
                .Callback(() => calls.Add("customer"))
                .Returns(new QBCustomer
                {
                    AccountNumber = request.CustomerAccountNumber,
                    Name = request.CustomerName
                });
            _mockRequestFactory
                .Setup(value => value.CreateCustomerAccountNumberQueryRequest(request.CustomerAccountNumber))
                .Returns(customer.Object);

            int itemReads = 0;
            var items = new Mock<IAllItemNonInvQueryRequest>();
            items.Setup(value => value.SendRequest())
                .Callback(() =>
                {
                    if (itemReads++ == 0) calls.Add("items");
                })
                .Returns(new QBStatusResponse<List<QBItem>>
                {
                    StatusCode = 0,
                    StatusMessage = "OK",
                    Data = new List<QBItem>()
                });
            _mockRequestFactory.Setup(value => value.CreateAllItemNonInvQueryRequest())
                .Returns(items.Object);

            var addItem = new Mock<IAddItemNonInventoryRequest>();
            addItem.Setup(value => value.SendRequest())
                .Callback(() => calls.Add("item-add"))
                .Returns(new List<QBStatusResponse<string>>
                {
                    new QBStatusResponse<string> { StatusCode = 0, StatusMessage = "OK" }
            });
            _mockRequestFactory.Setup(value => value.CreateAddItemNonInventoryRequest(
                    It.Is<List<QBItem>>(itemsToAdd =>
                        itemsToAdd.Count == 1 && itemsToAdd[0].Number == "1-NEW"),
                    writeFingerprint))
                .Returns(addItem.Object);
            ArrangeEstimateAdd(
                calls,
                "TXN-EST-GUARDED",
                request.QuoteNumber,
                writeFingerprint);

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(0, result.StatusCode, result.StatusMessage);
            CollectionAssert.AreEqual(
                new[]
                {
                    "company",
                    "estimate-query",
                    "customer",
                    "items",
                    "item-add",
                    "estimate-add"
                },
                calls);
        }

        [Test]
        public void QuoteWriteSafety_ConfirmedEstimateRetryReturnsIdentityWithoutAnyWrite()
        {
            var calls = new List<string>();
            QuickBooksService service = ServiceWithCompany(
                () => throw new AssertionException("Normal quotes must not read the company file."));
            QBQuoteUploadRequest request = QuoteRequest(QBQuoteTransactionType.Estimate, "normal");
            request.ConfirmedTransactionId = "TXN-CONFIRMED";
            ArrangeEstimateQuery(
                calls,
                request.QuoteNumber,
                new QBEstimateReference
                {
                    Reference = request.QuoteNumber,
                    TransactionId = "TXN-CONFIRMED"
                });

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(0, result.StatusCode);
            Assert.AreEqual("Estimate was already submitted.", result.StatusMessage);
            Assert.AreEqual("TXN-CONFIRMED", result.Data.TransactionId);
            Assert.AreEqual(request.QuoteNumber, result.Data.AssignedReference);
            CollectionAssert.AreEqual(new[] { "estimate-query" }, calls);
            AssertNoCustomerItemOrTransactionWrites();
        }

        [Test]
        public void QuoteWriteSafety_MultipleExactEstimateMatchesRequireReconciliationWithoutWrites()
        {
            var calls = new List<string>();
            QuickBooksService service = ServiceWithCompany(
                () => throw new AssertionException("Normal quotes must not read the company file."));
            QBQuoteUploadRequest request = QuoteRequest(QBQuoteTransactionType.Estimate, "normal");
            request.ConfirmedTransactionId = "TXN-CONFIRMED";
            var query = new Mock<IEstimateReferenceQueryRequest>();
            query.Setup(value => value.SendRequest())
                .Callback(() => calls.Add("estimate-query"))
                .Returns(new QBStatusResponse<List<QBEstimateReference>>
                {
                    StatusCode = 0,
                    StatusMessage = "OK",
                    Data = new List<QBEstimateReference>
                    {
                        new QBEstimateReference
                        {
                            Reference = request.QuoteNumber,
                            TransactionId = "TXN-CONFIRMED"
                        },
                        new QBEstimateReference
                        {
                            Reference = request.QuoteNumber,
                            TransactionId = "TXN-OTHER"
                        }
                    }
                });
            _mockRequestFactory
                .Setup(value => value.CreateEstimateReferenceQueryRequest(request.QuoteNumber))
                .Returns(query.Object);

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(1, result.StatusCode);
            Assert.AreEqual(
                "QuickBooks Estimate reconciliation is required.",
                result.StatusMessage);
            CollectionAssert.AreEqual(new[] { "estimate-query" }, calls);
            AssertNoCustomerItemOrTransactionWrites();
        }

        [TestCase(null)]
        [TestCase("TXN-DIFFERENT")]
        public void QuoteWriteSafety_AnyOtherExactEstimateMatchRequiresReconciliationBeforeWrites(
            string confirmedTransactionId)
        {
            var calls = new List<string>();
            QuickBooksService service = ServiceWithCompany(
                () => throw new AssertionException("Normal quotes must not read the company file."));
            QBQuoteUploadRequest request = QuoteRequest(QBQuoteTransactionType.Estimate, "normal");
            request.ConfirmedTransactionId = confirmedTransactionId;
            ArrangeEstimateQuery(
                calls,
                request.QuoteNumber,
                new QBEstimateReference
                {
                    Reference = request.QuoteNumber,
                    TransactionId = "TXN-EXISTING"
                });

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(1, result.StatusCode);
            Assert.AreEqual(
                "QuickBooks Estimate reconciliation is required.",
                result.StatusMessage);
            CollectionAssert.AreEqual(new[] { "estimate-query" }, calls);
            AssertNoCustomerItemOrTransactionWrites();
        }

        [Test]
        public void QuoteWriteSafety_EstimateQueryFailureIsGenericAndStopsBeforeWrites()
        {
            const string privateDetail = "Synthetic QBFC customer and transaction detail";
            var calls = new List<string>();
            QuickBooksService service = ServiceWithCompany(
                () => throw new AssertionException("Normal quotes must not read the company file."));
            QBQuoteUploadRequest request = QuoteRequest(QBQuoteTransactionType.Estimate, "normal");
            var query = new Mock<IEstimateReferenceQueryRequest>();
            query.Setup(value => value.SendRequest())
                .Callback(() => calls.Add("estimate-query"))
                .Throws(new InvalidOperationException(privateDetail));
            _mockRequestFactory
                .Setup(value => value.CreateEstimateReferenceQueryRequest(request.QuoteNumber))
                .Returns(query.Object);

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(1, result.StatusCode);
            Assert.AreEqual(
                "QuickBooks Estimate preflight is unavailable.",
                result.StatusMessage);
            StringAssert.DoesNotContain(privateDetail, result.StatusMessage);
            CollectionAssert.AreEqual(new[] { "estimate-query" }, calls);
            Assert.IsTrue(File.Exists(_logPath));
            StringAssert.DoesNotContain(privateDetail, File.ReadAllText(_logPath));
            AssertNoCustomerItemOrTransactionWrites();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("preview")]
        public void QuoteWriteSafety_InvalidQuoteKindFailsStructuralValidationBeforeQuickBooks(
            string quoteKind)
        {
            QuickBooksService service = ServiceWithCompany(
                () => throw new AssertionException("Invalid requests must not read the company file."));
            QBQuoteUploadRequest request = QuoteRequest(QBQuoteTransactionType.Estimate, quoteKind);

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(1, result.StatusCode);
            Assert.AreEqual(
                "Quote upload request QuoteKind must be normal or test",
                result.StatusMessage);
            AssertNoQuoteLookupsOrWrites();
        }

        [Test]
        public void QuoteWriteSafety_BlankEstimateReferenceFailsBeforeQuickBooks()
        {
            QuickBooksService service = ServiceWithCompany(
                () => throw new AssertionException("Invalid requests must not read the company file."));
            QBQuoteUploadRequest request = QuoteRequest(QBQuoteTransactionType.Estimate, "normal");
            request.QuoteNumber = " ";

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(1, result.StatusCode);
            Assert.AreEqual(
                "Quote upload request requires a QuoteNumber",
                result.StatusMessage);
            AssertNoQuoteLookupsOrWrites();
        }

        [TestCase("normal", "1", true)]
        [TestCase("normal", "99999999999", true)]
        [TestCase("normal", "F34552", true)]
        [TestCase("normal", "26-1001", true)]
        [TestCase("normal", "0", false)]
        [TestCase("normal", "100000000000", false)]
        [TestCase("normal", "\u0661", false)]
        [TestCase("normal", "TEST-ABC234", false)]
        [TestCase("normal", "test-ABC234", false)]
        [TestCase("normal", "ABC 123", false)]
        [TestCase("normal", "-------", false)]
        [TestCase("test", "TEST-ABC234", true)]
        [TestCase("test", "test-ABC234", false)]
        [TestCase("test", "TEST-ABC23", false)]
        [TestCase("test", "TEST-ABC230", false)]
        public void QuoteIdentityBoundary_SubmitQuoteValidatesKindReferencePair(
            string quoteKind,
            string quoteNumber,
            bool valid)
        {
            QuickBooksService service = ServiceWithCompany(
                () => throw new AssertionException(
                    "Identity validation must run before QuickBooks."));
            QBQuoteUploadRequest request = QuoteRequest(
                QBQuoteTransactionType.SalesOrder,
                quoteKind);
            request.QuoteNumber = quoteNumber;
            request.Lines = null;

            QBStatusResponse<QBQuoteUploadResult> result = service.SubmitQuote(request);

            Assert.AreEqual(1, result.StatusCode);
            Assert.AreEqual(
                valid
                    ? "Quote upload request must include at least one line"
                    : "Quote upload request QuoteNumber does not match QuoteKind",
                result.StatusMessage);
            AssertNoQuoteLookupsOrWrites();
        }

        [Test]
        public void QuoteWriteSafety_ExactEstimateQueryFactorySupportsCompanyBinding()
        {
            Assert.IsNotNull(typeof(IRequestFactory).GetMethod(
                "CreateEstimateReferenceQueryRequest",
                new[] { typeof(string), typeof(string) }));
        }

        private QuickBooksService ServiceWithCompany(Func<string> currentCompanyFileName)
        {
            return new QuickBooksService(
                _mockRequestFactory.Object,
                new Logger(_logPath),
                currentCompanyFileName,
                initialize: false);
        }

        private static QBQuoteUploadRequest QuoteRequest(
            QBQuoteTransactionType transactionType,
            string quoteKind)
        {
            return new QBQuoteUploadRequest
            {
                QuoteKind = quoteKind,
                TransactionType = transactionType,
                QuoteNumber = quoteKind == "test" ? "TEST-ABC234" : "120050",
                CustomerAccountNumber = "SYNTHETIC-100",
                CustomerName = "Synthetic Customer",
                CustomerPO = "PO-SYNTHETIC",
                DueDate = new DateTime(2026, 8, 14),
                Lines = new List<QBQuoteUploadLine>
                {
                    new QBQuoteUploadLine
                    {
                        Description = "Synthetic item",
                        Quantity = 1,
                        Rate = 10
                    }
                }
            };
        }

        private void ArrangeEstimateQuery(
            List<string> calls,
            string reference,
            QBEstimateReference match,
            string approvedCompanyFingerprint = null)
        {
            var query = new Mock<IEstimateReferenceQueryRequest>();
            query.Setup(value => value.SendRequest())
                .Callback(() => calls.Add("estimate-query"))
                .Returns(new QBStatusResponse<List<QBEstimateReference>>
                {
                    StatusCode = 0,
                    StatusMessage = "OK",
                    Data = match == null
                        ? new List<QBEstimateReference>()
                        : new List<QBEstimateReference> { match }
                });
            if (approvedCompanyFingerprint == null)
            {
                _mockRequestFactory
                    .Setup(value => value.CreateEstimateReferenceQueryRequest(reference))
                    .Returns(query.Object);
            }
            else
            {
                _mockRequestFactory
                    .Setup(value => value.CreateEstimateReferenceQueryRequest(
                        reference,
                        approvedCompanyFingerprint))
                    .Returns(query.Object);
            }
        }

        private void ArrangeCustomerAndItems(List<string> calls, QBQuoteUploadRequest request)
        {
            var customer = new Mock<ICustomerAccountNumberQueryRequest>();
            customer.Setup(value => value.SendRequest())
                .Callback(() => calls.Add("customer"))
                .Returns(new QBCustomer
                {
                    AccountNumber = request.CustomerAccountNumber,
                    Name = request.CustomerName
                });
            _mockRequestFactory
                .Setup(value => value.CreateCustomerAccountNumberQueryRequest(request.CustomerAccountNumber))
                .Returns(customer.Object);

            var items = new Mock<IAllItemNonInvQueryRequest>();
            items.Setup(value => value.SendRequest())
                .Callback(() => calls.Add("items"))
                .Returns(new QBStatusResponse<List<QBItem>>
                {
                    StatusCode = 0,
                    StatusMessage = "OK",
                    Data = new List<QBItem>
                    {
                        new QBItem
                        {
                            Number = "1-1000",
                            Description = request.Lines[0].Description,
                            Active = true
                        }
                    }
                });
            _mockRequestFactory
                .Setup(value => value.CreateAllItemNonInvQueryRequest())
                .Returns(items.Object);
        }

        private void ArrangeEstimateAdd(
            List<string> calls,
            string transactionId,
            string assignedReference,
            string approvedCompanyFingerprint)
        {
            var add = new Mock<IEstimateRequest>();
            add.Setup(value => value.SendRequest())
                .Callback(() => calls.Add("estimate-add"))
                .Returns(new QBStatusResponse<QBTransactionIdentity>
                {
                    StatusCode = 0,
                    StatusMessage = "OK",
                    Data = new QBTransactionIdentity
                    {
                        TransactionId = transactionId,
                        AssignedReference = assignedReference
                    }
                });
            _mockRequestFactory.Setup(value => value.CreateEstimateRequest(
                    It.IsAny<QBOrder>(),
                    approvedCompanyFingerprint))
                .Returns(add.Object);
        }

        private void ArrangeSalesOrderAdd(
            List<string> calls,
            string transactionId,
            string assignedReference,
            string approvedCompanyFingerprint)
        {
            var add = new Mock<ISalesOrderRequest>();
            add.Setup(value => value.SendRequest())
                .Callback(() => calls.Add("sales-order-add"))
                .Returns(new QBStatusResponse<QBTransactionIdentity>
                {
                    StatusCode = 0,
                    StatusMessage = "OK",
                    Data = new QBTransactionIdentity
                    {
                        TransactionId = transactionId,
                        AssignedReference = assignedReference
                    }
                });
            _mockRequestFactory.Setup(value => value.CreateSalesOrderRequest(
                    It.IsAny<QBOrder>(),
                    approvedCompanyFingerprint))
                .Returns(add.Object);
        }

        private void AssertNoQuoteLookupsOrWrites()
        {
            _mockRequestFactory.Verify(
                value => value.CreateEstimateReferenceQueryRequest(It.IsAny<string>()),
                Times.Never);
            AssertNoCustomerItemOrTransactionWrites();
        }

        private void AssertNoCustomerItemOrTransactionWrites()
        {
            _mockRequestFactory.Verify(
                value => value.CreateCustomerAccountNumberQueryRequest(It.IsAny<string>()),
                Times.Never);
            _mockRequestFactory.Verify(
                value => value.CreateCustomerQueryRequest(It.IsAny<string>()),
                Times.Never);
            _mockRequestFactory.Verify(
                value => value.CreateAllItemNonInvQueryRequest(),
                Times.Never);
            _mockRequestFactory.Verify(
                value => value.CreateAddItemNonInventoryRequest(
                    It.IsAny<List<QBItem>>(),
                    It.IsAny<string>()),
                Times.Never);
            _mockRequestFactory.Verify(
                value => value.CreateEstimateRequest(
                    It.IsAny<QBOrder>(),
                    It.IsAny<string>()),
                Times.Never);
            _mockRequestFactory.Verify(
                value => value.CreateSalesOrderRequest(
                    It.IsAny<QBOrder>(),
                    It.IsAny<string>()),
                Times.Never);
        }

        [Test]
        public void Constructor_WithNullRequestFactory_ShouldThrowArgumentNullException()
        {
            // Arrange, Act & Assert
            Assert.Throws<ArgumentNullException>(() => new QuickBooksService(null));
        }

        [Test]
        public void CommercialTermsCache_FailedRefreshPreservesLastGoodCatalog()
        {
            Type cacheType = typeof(QuickBooksService).Assembly.GetType(
                "QuickBooksIPCService.CommercialTermsCache");
            Assert.IsNotNull(cacheType, "CommercialTermsCache must exist");
            object cache = Activator.CreateInstance(cacheType);
            var replace = cacheType.GetMethod("TryReplace");
            var read = cacheType.GetMethod("Read");
            Assert.IsNotNull(replace);
            Assert.IsNotNull(read);

            bool first = (bool)replace.Invoke(cache, new object[]
            {
                0,
                new[] { "Prepaid", "Net 30" },
                new[] { "UPS Ground" },
                new DateTime(2026, 8, 13, 18, 0, 0, DateTimeKind.Utc)
            });
            bool failed = (bool)replace.Invoke(cache, new object[]
            {
                1,
                new[] { "should not replace" },
                new[] { "should not replace" },
                new DateTime(2026, 8, 14, 18, 0, 0, DateTimeKind.Utc)
            });
            object snapshot = read.Invoke(cache, null);

            Assert.IsTrue(first);
            Assert.IsFalse(failed);
            CollectionAssert.AreEqual(
                new[] { "Prepaid", "Net 30" },
                (IEnumerable<string>)snapshot.GetType().GetProperty("CreditTerms").GetValue(snapshot));
            CollectionAssert.AreEqual(
                new[] { "UPS Ground" },
                (IEnumerable<string>)snapshot.GetType().GetProperty("ShippingMethods").GetValue(snapshot));
            Assert.AreEqual(
                new DateTime(2026, 8, 13, 18, 0, 0, DateTimeKind.Utc),
                snapshot.GetType().GetProperty("RefreshedAtUtc").GetValue(snapshot));
        }

        [Test]
        public void GetCommercialTerms_WithoutCachedCatalogReturnsGenericUnavailableResponse()
        {
            var method = typeof(QuickBooksService).GetMethod("GetCommercialTerms");
            Assert.IsNotNull(method, "QuickBooksService.GetCommercialTerms must exist");

            object response = method.Invoke(_service, null);

            Assert.AreNotEqual(0, response.GetType().GetProperty("StatusCode").GetValue(response));
            Assert.AreEqual(
                "QuickBooks commercial terms are unavailable.",
                response.GetType().GetProperty("StatusMessage").GetValue(response));
            Assert.IsNull(response.GetType().GetProperty("Data").GetValue(response));
        }

        [Test]
        public void GetCommercialTerms_ReadsBackgroundCacheWithoutQueryingQuickBooks()
        {
            var refreshedAt = new DateTime(2026, 8, 13, 18, 0, 0, DateTimeKind.Utc);
            var query = new Mock<ICommercialTermsQueryRequest>();
            query.Setup(value => value.SendRequest()).Returns(
                new QBStatusResponse<QBCommercialTermsCatalog>
                {
                    StatusCode = 0,
                    StatusMessage = "Status OK",
                    Data = new QBCommercialTermsCatalog
                    {
                        CreditTerms = new List<string> { "Prepaid", "Net 30" },
                        ShippingMethods = new List<string> { "UPS Ground" },
                        RefreshedAtUtc = refreshedAt
                    }
                });
            _mockRequestFactory
                .Setup(value => value.CreateCommercialTermsQueryRequest())
                .Returns(query.Object);

            var update = typeof(QuickBooksService).GetMethod(
                "UpdateCommercialTermsCache",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.IsTrue((bool)update.Invoke(_service, null));

            QBStatusResponse<QBCommercialTermsCatalog> first = _service.GetCommercialTerms();
            QBStatusResponse<QBCommercialTermsCatalog> cached = _service.GetCommercialTerms();

            Assert.AreEqual(0, first.StatusCode);
            CollectionAssert.AreEqual(new[] { "Prepaid", "Net 30" }, first.Data.CreditTerms);
            CollectionAssert.AreEqual(new[] { "UPS Ground" }, first.Data.ShippingMethods);
            Assert.AreEqual(refreshedAt, first.Data.RefreshedAtUtc);
            Assert.AreEqual(0, cached.StatusCode);
            query.Verify(value => value.SendRequest(), Times.Once);
        }

        [Test]
        public void GetCustomerCommercialTerms_ReadsBackgroundCacheEvenWhenRefreshIsRequested()
        {
            var query = new Mock<ICustomerCommercialTermsQueryRequest>();
            query.Setup(value => value.SendRequest())
                .Returns(new QBStatusResponse<List<QBCustomerCommercialTermsRecord>>
                {
                    StatusCode = 0,
                    Data = new List<QBCustomerCommercialTermsRecord>
                    {
                        new QBCustomerCommercialTermsRecord
                        {
                            AccountNumber = "EX-1042",
                            CreditTerms = "Net 30"
                        }
                    }
                });
            _mockRequestFactory
                .Setup(value => value.CreateCustomerCommercialTermsQueryRequest())
                .Returns(query.Object);

            var update = typeof(QuickBooksService).GetMethod(
                "UpdateCustomerCommercialTermsCache",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.IsTrue((bool)update.Invoke(_service, null));

            QBStatusResponse<QBCustomerCommercialTerms> first =
                _service.GetCustomerCommercialTerms(" EX-1042 ", false);
            QBStatusResponse<QBCustomerCommercialTerms> cached =
                _service.GetCustomerCommercialTerms("EX-1042", false);
            QBStatusResponse<QBCustomerCommercialTerms> refreshed =
                _service.GetCustomerCommercialTerms("EX-1042", true);

            Assert.AreEqual("Net 30", first.Data.CreditTerms);
            Assert.AreEqual("Net 30", cached.Data.CreditTerms);
            Assert.AreEqual("Net 30", refreshed.Data.CreditTerms);
            query.Verify(value => value.SendRequest(), Times.Once);
        }
    }
}
