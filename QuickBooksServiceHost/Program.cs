// QuickBooksServiceHost/Program.cs
using System;
using System.Configuration;
using System.ServiceModel;
using QuickBooksIPCService;
using QuickBooksIPCContracts;
using System.ServiceModel.Description;

namespace QuickBooksServiceHost
{
    class Program
    {
        static void Main(string[] args)
        {
            // App setting comes from a shared config file in the solution directory, fyi
            string baseAddress = ConfigurationManager.AppSettings["QuickBooksServiceBaseAddress"];

            using (ServiceHost host = new ServiceHost(typeof(QuickBooksService), new Uri(baseAddress)))
            {
                NetNamedPipeBinding binding = new NetNamedPipeBinding();
                host.AddServiceEndpoint(typeof(IQuickBooksService), binding, "");

                // Optional: Enable metadata exchange (for client proxies)
                ServiceMetadataBehavior smb = new ServiceMetadataBehavior();
                host.Description.Behaviors.Add(smb);
                host.AddServiceEndpoint(ServiceMetadataBehavior.MexContractName, MetadataExchangeBindings.CreateMexNamedPipeBinding(), "mex");

                try
                {
                    host.Open();
                    Console.WriteLine("WCF Service is running...");
                    Console.WriteLine("Press ENTER to exit.");
                    Console.ReadLine();
                    host.Close();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"An exception occurred: {ex.Message}");
                    host.Abort();
                }
            }
        }
    }
}
