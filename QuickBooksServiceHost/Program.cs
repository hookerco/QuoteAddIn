// QuickBooksServiceHost/Program.cs
using System;
using System.Configuration;
using System.ServiceModel;
using QuickBooksIPCService;
using QuickBooksIPCContracts;
using System.ServiceModel.Description;
using System.Threading;
using System.Diagnostics;


namespace QuickBooksServiceHost
{
    class Program
    {
        static void Main(string[] args)
        {
            // App setting comes from a shared config file in the solution directory, fyi
            string baseAddress = "net.pipe://localhost/QuickBooksService";

            QuickBooksService serviceInstance = new QuickBooksService();

            using (ServiceHost host = new ServiceHost(serviceInstance, new Uri(baseAddress)))
            {
                NetNamedPipeBinding binding = new NetNamedPipeBinding();
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
                    Console.WriteLine("WCF Service is running...");

                    Stopwatch sw = new Stopwatch();
                    Console.WriteLine("Preloading cache...");
                    sw.Start();
                    serviceInstance.GetAllItems(); // Preload the cache
                    sw.Stop();
                    Console.WriteLine($"Cache preloaded in {sw.ElapsedMilliseconds}ms");

                    // Use ManualResetEvent to keep the process alive
                    ManualResetEvent shutdownEvent = new ManualResetEvent(false);
                    Console.CancelKeyPress += (sender, eventArgs) =>
                    {
                        eventArgs.Cancel = true;
                        shutdownEvent.Set();
                    };

                    shutdownEvent.WaitOne();

                    host.Close();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"An exception occurred: {ex.Message}");
                    host.Abort();
                }
            }
        }
    }
}
