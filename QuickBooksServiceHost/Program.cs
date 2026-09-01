// QuickBooksServiceHost/Program.cs
using System;
using System.Configuration;
using System.ServiceModel;
using QuickBooksIPCService;
using QuickBooksIPCContracts;
using System.ServiceModel.Description;
using System.Threading;
using System.Diagnostics;
using QuickBooksConnectorCore;

using QBRequestLibrary;


namespace QuickBooksServiceHost
{
    class Program
    {
        static void Main(string[] args)
        {
            string baseAddress = "net.pipe://localhost/QuickBooksService";

            using (var service = new QuickBooksService())
            using (ServiceHost host = new ServiceHost(service, new Uri(baseAddress)))
            {
                NetNamedPipeBinding binding = QuickBooksPipeBindingFactory.CreateServiceBinding();
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

                    Console.WriteLine("Service Host started. Loading QuickBooks data...");
                    Console.WriteLine("Press R to retry the refresh, or Ctrl+C to stop.");
                    WaitForShutdownOrRefresh(shutdownEvent, service);

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

        private static void WaitForShutdownOrRefresh(
            ManualResetEvent shutdownEvent,
            QuickBooksService service)
        {
            while (!shutdownEvent.WaitOne(250))
            {
                try
                {
                    if (!Console.IsInputRedirected && Console.KeyAvailable &&
                        Console.ReadKey(intercept: true).Key == ConsoleKey.R)
                    {
                        Console.WriteLine("Refreshing QuickBooks data...");
                        var result = service.RefreshQuickBooksCaches();
                        Console.WriteLine(result.StatusMessage);
                    }
                }
                catch (InvalidOperationException)
                {
                    shutdownEvent.WaitOne();
                    return;
                }
                catch (System.IO.IOException)
                {
                    shutdownEvent.WaitOne();
                    return;
                }
            }
        }
    }
}
