using QuickBooksIPCContracts;
using System;
using System.ServiceModel;
using System.Xml;

namespace ExcelAddIn1.SendToQb
{
    internal class QBConnector : IDisposable
    {
        private const string ServiceBaseAddress = "net.pipe://localhost/QuickBooksService";
        private readonly QBServiceHost _serviceHost;
        private IQuickBooksService _client;
        private ChannelFactory<IQuickBooksService> _channelFactory;

        public QBConnector(string serviceExecutablePath)
        {
            // Initialize and start the service
            _serviceHost = new QBServiceHost(serviceExecutablePath);
            _serviceHost.StartService();

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

            _serviceHost?.Dispose();
        }
    }
}
