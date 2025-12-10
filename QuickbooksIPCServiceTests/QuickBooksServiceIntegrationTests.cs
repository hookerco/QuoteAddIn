// QuickBooksServiceLibrary.IntegrationTests/QuickBooksServiceIntegrationTests.cs
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.ServiceModel;
using System.Threading;
using QuickBooksIPCContracts;

namespace QuickBooksServiceLibrary.IntegrationTests
{
    [TestFixture]
    public class QuickBooksServiceIntegrationTests
    {
        private const string ServiceExecutableName = "QuickBooksServiceHost.exe";
        private const string ServiceBaseAddress = "net.pipe://localhost/QuickBooksService";

        private Process _serviceProcess;
        private IQuickBooksService _client;
        private ChannelFactory<IQuickBooksService> _channelFactory;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            NetNamedPipeBinding binding = new NetNamedPipeBinding
            {
                MaxReceivedMessageSize = 2147483647,
                ReaderQuotas = new System.Xml.XmlDictionaryReaderQuotas
                {
                    MaxDepth = 32,
                    MaxStringContentLength = 2147483647,
                    MaxArrayLength = 2147483647,
                    MaxBytesPerRead = 4096,
                    MaxNameTableCharCount = 2147483647
                },
                // Optional: increase binding-level timeouts as well
                OpenTimeout = TimeSpan.FromSeconds(30),
                SendTimeout = TimeSpan.FromMinutes(5),
                ReceiveTimeout = TimeSpan.FromMinutes(5),
                CloseTimeout = TimeSpan.FromSeconds(30)
            };

            EndpointAddress endpointAddress = new EndpointAddress(ServiceBaseAddress);
            _channelFactory = new ChannelFactory<IQuickBooksService>(binding, endpointAddress);

            // Create the channel and increase per-operation timeout
            var clientChannel = (IClientChannel)_channelFactory.CreateChannel();
            clientChannel.OperationTimeout = TimeSpan.FromMinutes(5); // or whatever is reasonable
            _client = (IQuickBooksService)clientChannel;
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            // Close the client channel
            if (_client != null)
            {
                try
                {
                    ((IClientChannel)_client).Close();
                }
                catch
                {
                    ((IClientChannel)_client).Abort();
                }
            }

            // Close the channel factory
            if (_channelFactory != null)
            {
                try
                {
                    _channelFactory.Close();
                }
                catch
                {
                    _channelFactory.Abort();
                }
            }

            // Terminate the service process
            if (_serviceProcess != null && !_serviceProcess.HasExited)
            {
                try
                {
                    // Gracefully close the service by sending an ENTER key press
                    // Since the host is a console app waiting for ENTER, you might need to automate this
                    // Alternatively, kill the process
                    _serviceProcess.Kill();
                    _serviceProcess.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error terminating service process: {ex.Message}");
                }
                finally
                {
                    _serviceProcess.Dispose();
                }
            }
        }

        /// <summary>
        /// Waits until the WCF service is ready to accept requests.
        /// </summary>
        /// <param name="timeout">Maximum time to wait for the service to become ready.</param>
        /// <returns>True if the service is ready; otherwise, false.</returns>
        private bool WaitForServiceReady(TimeSpan timeout)
        {
            DateTime endTime = DateTime.Now + timeout;
            while (DateTime.Now < endTime)
            {
                try
                {
                    // Attempt to open a channel; if successful, the service is ready
                    using (var testChannelFactory = new ChannelFactory<IQuickBooksService>(new NetNamedPipeBinding(), new EndpointAddress(ServiceBaseAddress)))
                    {
                        IQuickBooksService testClient = testChannelFactory.CreateChannel();

                        // Simple call to verify service availability
                        testClient.Ping();
                        return true;
                    }
                    
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Service not ready: {ex.Message}");
                    // Service not ready yet; wait and retry
                    Thread.Sleep(500);
                }
            }
            return false;
        }

        [Test]
        public void AddOrder_ShouldReturnSuccessResponse()
        {
            // Arrange
            var order = new QBOrder
            {
                Customer = new QBCustomer { Name = "DummyCustomer" },
                DueDate = DateTime.Now,
                Items = new List<QBItem>
                {
                    new QBItem { Number = "Item1", Quantity = 2, Rate = 10.5 },
                    new QBItem { Number = "Item2", Quantity = 1, Rate = 20 }
                }
            };

            // Act
            QBStatusResponse<string> response = null;
            try
            {
                response = _client.AddOrder(order);
            }
            catch (FaultException ex)
            {
                Assert.Fail($"Service threw a fault exception: {ex.Message}");
            }
            catch (Exception ex)
            {
                Assert.Fail($"Service threw an unexpected exception: {ex.Message}");
            }

            // Assert
            Assert.IsNotNull(response, "Response should not be null.");
            Assert.AreEqual(0, response.StatusCode, "StatusCode should be 0 indicating success.");
        }

        [Test]
        public void GetCustomer_ShouldReturnCustomer()
        {
            // Arrange
            string accountNumber = "12345";

            // Act
            QBCustomer customer = null;
            try
            {
                customer = _client.GetCustomer(accountNumber);
            }
            catch (FaultException ex)
            {
                Assert.Fail($"Service threw a fault exception: {ex.Message}");
            }
            catch (Exception ex)
            {
                Assert.Fail($"Service threw an unexpected exception: {ex.Message}");
            }

            // Assert
            Assert.IsNotNull(customer, "Customer should not be null.");
            Assert.AreEqual(accountNumber, customer.AccountNumber, "AccountNumber should match the requested value.");
            Assert.AreEqual("INDTUBECOM12345", customer.Name, "Customer Name should match expected value.");
        }

        [Test]
        public void GetAllItems_ShouldReturnListOfItems()
        {
            // Arrange

            // Act
            QBStatusResponse<List<QBItem>> response = null;
            try
            {
                response = _client.GetAllItems();
            }
            catch (FaultException ex)
            {
                Assert.Fail($"Service threw a fault exception: {ex.Message}");
            }
            catch (Exception ex)
            {
                Assert.Fail($"Service threw an unexpected exception: {ex.Message}");
            }

            // Assert
            Assert.IsNotNull(response, "Response should not be null.");
            Assert.AreEqual(0, response.StatusCode, "StatusCode should be 0 indicating success.");
            //Assert.AreEqual("Items Retrieved Successfully", response.StatusMessage, "StatusMessage should indicate success.");
            Assert.IsNotNull(response.Data, "Data should not be null.");
            Assert.IsNotEmpty(response.Data, "Data should contain at least one item.");
            foreach (var item in response.Data)
            {
                Assert.IsNotNull(item.Number, "Item Number should not be null.");
                Assert.IsNotNull(item.Description, "Item Description should not be null.");
                Assert.IsNotNull(item.Active, "Item Active should not be null.");
            }
        }

        [Test]
        public void AddNonInvItem_ShouldReturnSuccessResponses()
        {
            // Arrange
            var items = new List<QBItem>
            {
                new QBItem { Number = "Item1", Description = "Description1", AccountName = "Sales Income" },
                new QBItem { Number = "Item2", Description = "Description2", AccountName = "Sales Income" }
            };

            // Act
            List<QBStatusResponse<string>> responses = null;
            try
            {
                responses = _client.AddNonInvItem(items);
            }
            catch (FaultException ex)
            {
                Assert.Fail($"Service threw a fault exception: {ex.Message}");
            }
            catch (Exception ex)
            {
                Assert.Fail($"Service threw an unexpected exception: {ex.Message}");
            }

            // Assert
            Assert.IsNotNull(responses, "Responses should not be null.");
            Assert.AreEqual(2, responses.Count, "There should be two responses.");
            Assert.AreEqual(0, responses[0].StatusCode, "First response StatusCode should be 0 indicating success.");
            Assert.AreEqual("Status OK", responses[0].StatusMessage, "First response StatusMessage should indicate success.");
            Assert.AreEqual(0, responses[1].StatusCode, "Second response StatusCode should be 0 indicating success.");
            Assert.AreEqual("Status OK", responses[1].StatusMessage, "Second response StatusMessage should indicate success.");

            // remove items afterward?
        }
    }
}
