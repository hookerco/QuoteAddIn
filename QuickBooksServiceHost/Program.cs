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
            string baseAddress = "net.pipe://localhost/QuickBooksService";

            using (ServiceHost host = new ServiceHost(typeof(QuickBooksService), new Uri(baseAddress)))
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
                    //Console.WriteLine("Press enter to end the service");
                    //Console.ReadLine();
                    host.Abort();
                }
            }
        }
    }
}
