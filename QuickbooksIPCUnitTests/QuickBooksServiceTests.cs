// QuickBooksServiceLibrary.Tests/QuickBooksServiceTests.cs
using NUnit.Framework;
using Moq;
using QBRequestLibrary;
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

        [SetUp]
        public void Setup()
        {
            _mockRequestFactory = new Mock<IRequestFactory>();
            var logPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "logs", "QuickBooksServiceTests.log");
            _service = new QuickBooksService(_mockRequestFactory.Object, new Logger(logPath), initialize: false);
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

            var expectedResponse = new QBStatusResponse<string>
            {
                StatusCode = 0,
                StatusMessage = "Order Added Successfully"
            };

            var mockSalesOrderRequest = new Mock<ISalesOrderRequest>();
            mockSalesOrderRequest.Setup(r => r.SendRequest()).Returns(expectedResponse);

            _mockRequestFactory
                .Setup(f => f.CreateSalesOrderRequest(order))
                .Returns(mockSalesOrderRequest.Object);

            // Act
            var result = _service.AddOrder(order);

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.AreEqual(expectedResponse.StatusCode, result.StatusCode);
            Assert.AreEqual(expectedResponse.StatusMessage, result.StatusMessage);

            _mockRequestFactory.Verify(f => f.CreateSalesOrderRequest(order), Times.Once);
            mockSalesOrderRequest.Verify(r => r.SendRequest(), Times.Once);
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
                .Setup(f => f.CreateAddItemNonInventoryRequest(items))
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

            _mockRequestFactory.Verify(f => f.CreateAddItemNonInventoryRequest(items), Times.Once);
            mockAddItemNonInventoryRequest.Verify(r => r.SendRequest(), Times.Once);
        }

        [Test]
        public void Constructor_WithNullRequestFactory_ShouldThrowArgumentNullException()
        {
            // Arrange, Act & Assert
            Assert.Throws<ArgumentNullException>(() => new QuickBooksService(null));
        }
    }
}
