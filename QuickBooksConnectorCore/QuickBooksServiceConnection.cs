using System;
using System.ServiceModel;
using System.Xml;
using QuickBooksIPCContracts;

namespace QuickBooksConnectorCore
{
    /// <summary>
    /// A short-lived client of the local QuickBooks WCF service exposed over the
    /// NetNamedPipe at <c>net.pipe://localhost/QuickBooksService</c>. Each call opens
    /// its own connection; the CLI and the localhost bridge are both just local
    /// clients of the always-on service host process.
    /// </summary>
    public sealed class QuickBooksServiceConnection : IDisposable
    {
        public const string ServiceBaseAddress = "net.pipe://localhost/QuickBooksService";

        private static readonly TimeSpan ServiceOperationTimeout = TimeSpan.FromMinutes(5);
        private readonly ChannelFactory<IQuickBooksService> _channelFactory;

        public QuickBooksServiceConnection()
        {
            var binding = new NetNamedPipeBinding
            {
                OpenTimeout = TimeSpan.FromSeconds(30),
                CloseTimeout = TimeSpan.FromSeconds(30),
                SendTimeout = ServiceOperationTimeout,
                ReceiveTimeout = ServiceOperationTimeout,
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

            _channelFactory = new ChannelFactory<IQuickBooksService>(binding, new EndpointAddress(ServiceBaseAddress));
            Client = _channelFactory.CreateChannel();
            ((IContextChannel)Client).OperationTimeout = ServiceOperationTimeout;
        }

        public IQuickBooksService Client { get; private set; }

        public void Dispose()
        {
            CloseCommunicationObject(Client as ICommunicationObject);
            CloseCommunicationObject(_channelFactory);
        }

        private static void CloseCommunicationObject(ICommunicationObject communicationObject)
        {
            if (communicationObject == null)
            {
                return;
            }

            try
            {
                if (communicationObject.State != CommunicationState.Closed)
                {
                    communicationObject.Close();
                }
            }
            catch
            {
                communicationObject.Abort();
            }
        }
    }
}
