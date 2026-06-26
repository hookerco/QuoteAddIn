// QuickBooksServiceHost/Program.cs
using System;
using System.Configuration;
using System.ServiceModel;
using QuickBooksIPCService;
using QuickBooksIPCContracts;
using System.ServiceModel.Description;
using System.Threading;
using System.Diagnostics;

using QBRequestLibrary;


namespace QuickBooksServiceHost
{
    class Program
    {
        private static readonly TimeSpan ServiceOperationTimeout = TimeSpan.FromMinutes(5);

        static void Main(string[] args)
        {
            string baseAddress = "net.pipe://localhost/QuickBooksService";

            using (ServiceHost host = new ServiceHost(typeof(QuickBooksService), new Uri(baseAddress)))
            {
                NetNamedPipeBinding binding = new NetNamedPipeBinding
                {
                    OpenTimeout = TimeSpan.FromSeconds(30),
                    CloseTimeout = TimeSpan.FromSeconds(30),
                    SendTimeout = ServiceOperationTimeout,
                    ReceiveTimeout = ServiceOperationTimeout
                };
                host.AddServiceEndpoint(typeof(IQuickBooksService), binding, "");

                // Optional: Enable metadata exchange (for client proxies)
                ServiceMetadataBehavior smb = new ServiceMetadataBehavior
                {
                    HttpGetEnabled = false
                };
                host.Description.Behaviors.Add(smb);
                host.AddServiceEndpoint(typeof(IMetadataExchange), MetadataExchangeBindings.CreateMexNamedPipeBinding(), "mex");
                try
                {
                    host.Open();

                    // Start the QuickBooks localhost bridge (HTTP listener on 127.0.0.1) that lets
                    // the centrally-hosted web quote module send into this machine's open QB seat.
                    // A bridge failure (e.g. port in use) must not take down the pipe service.
                    QbBridge bridge = null;
                    try
                    {
                        bridge = QbBridge.Start();
                    }
                    catch (Exception bridgeEx)
                    {
                        Console.Error.WriteLine($"Failed to start QuickBooks localhost bridge: {bridgeEx.Message}");
                    }

                    // Use ManualResetEvent to keep the process alive
                    ManualResetEvent shutdownEvent = new ManualResetEvent(false);
                    Console.CancelKeyPress += (sender, eventArgs) =>
                    {
                        eventArgs.Cancel = true;
                        shutdownEvent.Set();
                    };


                    shutdownEvent.WaitOne();

                    bridge?.Stop();
                    host.Close();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"An exception occurred: {ex.Message}");
                    //Console.WriteLine("Press enter to end the service");
                    //Console.ReadLine();
                    host.Abort();
                }
            }
        }
    }
}
