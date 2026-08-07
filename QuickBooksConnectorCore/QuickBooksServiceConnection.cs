using System;
using System.ServiceModel;
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
            var binding = QuickBooksPipeBindingFactory.CreateClientBinding();

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
