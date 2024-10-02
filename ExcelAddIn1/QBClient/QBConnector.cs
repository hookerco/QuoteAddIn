using QuickBooksIPCContracts;
using System;
using System.ServiceModel;
using System.Xml;
using System.Threading; 
using System.Configuration;

namespace ExcelAddIn1.SendToQb
{
    internal class QBConnector : IDisposable
    {
        private readonly string ServiceBaseAddress = "net.pipe://localhost/QuickBooksService";
         //private readonly QBServiceHost _serviceHost;
        private readonly IQuickBooksService _client;
        private readonly ChannelFactory<IQuickBooksService> _channelFactory;

        public QBConnector()
        {
            //// Initialize and start the service
            //_serviceHost = new QBServiceHost();
            //_serviceHost.StartService();

            //// Wait for the service to start
            //bool serviceStarted = WaitForServiceStart(TimeSpan.FromSeconds(3));
            //if (serviceStarted)
            //{
            //    Console.WriteLine("Service started successfully.");
            //}
            //else
            //{
            //    Console.WriteLine("Service did not start within the specified timeout.");
            //}

            // Set up the WCF client
            var binding = new NetNamedPipeBinding
            {
                MaxReceivedMessageSize = int.MaxValue,
                ReaderQuotas = new XmlDictionaryReaderQuotas
                {
                    MaxDepth = 32,
                    MaxStringContentLength = int.MaxValue,
                    MaxArrayLength = int.MaxValue,
                    MaxBytesPerRead = 4096,
                    MaxNameTableCharCount = int.MaxValue
                }
            };

            var endpointAddress = new EndpointAddress(ServiceBaseAddress);
            _channelFactory = new ChannelFactory<IQuickBooksService>(binding, endpointAddress);
            _client = _channelFactory.CreateChannel();
        }

        //private bool WaitForServiceStart(TimeSpan timeout)
        //{
        //    DateTime startTime = DateTime.Now;
        //    while (!_serviceHost.IsServiceRunning())
        //    {
        //        if (DateTime.Now - startTime > timeout)
        //        {
        //            return false; // Timeout reached
        //        }

        //        // Wait for a short period before checking again
        //        Thread.Sleep(500);
        //    }

        //    return true; // Service started within the timeout
        //}

        /// <summary>
        /// Provides access to the WCF service client.
        /// </summary>
        public IQuickBooksService Client => _client;

        /// <summary>
        /// Disposes the QBConnector, ensuring the WCF channel and service are properly closed.
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (_channelFactory != null)
                {
                    if (_channelFactory.State != CommunicationState.Closed)
                        _channelFactory.Close();
                }
            }
            catch
            {
                _channelFactory.Abort();
            }

            //_serviceHost?.Dispose();
        }
    }
}
