// QuickBooksIPCService/QuickBooksService.cs
using System;
using System.ServiceModel;
using System.Collections.Generic;
using QBRequestLibrary;
using QuickBooksIPCContracts;
using System.Data.SqlClient;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;

namespace QuickBooksIPCService
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    public class QuickBooksService : IQuickBooksService
    {
        private readonly IRequestFactory _requestFactory;
        private static readonly Dictionary<string, QBStatusResponse<List<QBItem>>> _cache
            = new Dictionary<string, QBStatusResponse<List<QBItem>>>();
        private bool _cacheValid = false;
        private readonly Logger _logger;
        private bool _disposed = false;

        public QuickBooksService(IRequestFactory requestFactory)
        {
            _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
            _logger = new Logger();
            _initialize();
        }

        public QuickBooksService(IRequestFactory requestFactory, Logger logger, bool initialize = true)
        {
            _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (initialize)
            {
                _initialize();
            }
        }

        public QuickBooksService()
            : this(new RequestFactory(), new Logger())
        {
        }

        private void _initialize()
        {
            _logger.LogSessionStart();
            Task.Run(() => UpdateCache(background: true, initial: true));
            Task.Run(() => AutoUpdateCache(60));
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    _logger.LogSessionEnd();
                }
                // Dispose unmanaged resources if any
                _disposed = true;
            }
        }

        ~QuickBooksService()
        {
            Dispose(false);
        }

        public string Ping()
        {
            return "Pong";
        }

        public QBStatusResponse<string> AddOrder(QBOrder order)
        {
            var req = _requestFactory.CreateSalesOrderRequest(order);
            var response = req.SendRequest();
            _logger.LogTransaction($"Added order {order.QuoteNumber} for customer {order.Customer.Name}");
            return response;
        }

        public QBStatusResponse<string> AddEstimate(QBOrder order)
        {
            var req = _requestFactory.CreateEstimateRequest(order);
            var response = req.SendRequest();
            _logger.LogTransaction($"Added estimate {order.QuoteNumber} for customer {order.Customer.Name}");
            return response;
        }

        public QBCustomer GetCustomer(string accountNumber)
        {
            var req = _requestFactory.CreateCustomerQueryRequest(accountNumber);
            var response = req.SendRequest();
            _logger.LogTransaction($"Retrieved customer {response.Name} with account number {response.AccountNumber}");
            return response;
        }

        public QBStatusResponse<List<QBItem>> GetAllItems()
        {
            // Check if cache is still valid
            if (!_cacheValid)
            {
                // Perform the query (this is the long-running part)
                UpdateCache(background: false);
                _logger.LogInfo("Updated cache in main thread");
            }

            _logger.LogTransaction("Retrieved all items from cache");
            return _cache["AllItemInventory"];
        }

        private void UpdateCache(bool background, bool initial = false)
        {
            if (initial)
            {
                Console.WriteLine("Updating cache, service not ready");
            }
            var req = _requestFactory.CreateAllItemNonInvQueryRequest();

            Stopwatch sw = new Stopwatch();
            sw.Start();
            var result = req.SendRequest();
            sw.Stop();
            var elapsedSeconds = (double)sw.ElapsedMilliseconds / 1000;

            lock (_cache)
            {
                _cache["AllItemInventory"] = result;
                _cacheValid = true;
            }

            _logger.LogInfo($"Updated cache in {(background ? "background" : "main thread")}: {elapsedSeconds} seconds");
            if (initial)
            {
                Console.WriteLine($"WCF Service is running... {elapsedSeconds} second start-up");
            }
        }

        private void AutoUpdateCache(int min_interval)
        {
            int ms_interval = min_interval * 60 * 1000;
            while (true)
            {
                Thread.Sleep(ms_interval);
                UpdateCache(background: true);
            }
        }

        public int InvalidateAllItemsCache()
        {
            _cacheValid = false;
            _logger.LogInfo("Invalidated cache");
            return 0;
        }

        public List<QBStatusResponse<string>> AddNonInvItem(List<QBItem> items)
        {
            var req = _requestFactory.CreateAddItemNonInventoryRequest(items);
            var response = req.SendRequest();
            foreach (var rs in response)
            {
                if (rs.StatusCode != 0)
                {
                    _logger.LogError($"Failed to add item {rs.Data} with error: {rs.StatusMessage}");
                }
            }

            InvalidateAllItemsCache();
            Task.Run(() => UpdateCache(background: true));
            _logger.LogInfo("Updated cache in background");

            return response;
        }
    }
}
