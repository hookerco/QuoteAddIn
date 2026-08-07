using System;
using System.Net.Security;
using System.ServiceModel;
using System.Xml;

namespace QuickBooksConnectorCore
{
    /// <summary>
    /// Defines the shared local named-pipe contract used by the service host and its clients.
    /// </summary>
    public static class QuickBooksPipeBindingFactory
    {
        private static readonly TimeSpan ServiceOperationTimeout = TimeSpan.FromMinutes(5);

        public static NetNamedPipeBinding CreateServiceBinding()
        {
            var binding = new NetNamedPipeBinding
            {
                OpenTimeout = TimeSpan.FromSeconds(30),
                CloseTimeout = TimeSpan.FromSeconds(30),
                SendTimeout = ServiceOperationTimeout,
                ReceiveTimeout = ServiceOperationTimeout
            };

            ConfigureLocalCrossIntegritySecurity(binding);
            return binding;
        }

        public static NetNamedPipeBinding CreateClientBinding()
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

            ConfigureLocalCrossIntegritySecurity(binding);
            return binding;
        }

        private static void ConfigureLocalCrossIntegritySecurity(NetNamedPipeBinding binding)
        {
            binding.Security.Mode = NetNamedPipeSecurityMode.Transport;
            binding.Security.Transport.ProtectionLevel = ProtectionLevel.None;
        }
    }
}
