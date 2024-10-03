// QuickBooksIPCService/QuickBooksService.cs
using System;
using System.ServiceModel;
using System.Collections.Generic;
using QBRequestLibrary;
using QuickBooksIPCContracts;
using System.Data.SqlClient;
using System.Threading.Tasks;
using System.Threading;

namespace QuickBooksIPCService
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    public class QuickBooksService : IQuickBooksService
    {
        private readonly IRequestFactory _requestFactory;
        private static readonly Dictionary<string, QBStatusResponse<List<QBItem>>> _cache
            = new Dictionary<string, QBStatusResponse<List<QBItem>>>();
        private bool _cacheValid = false;
        private readonly Logger _logger = new Logger();

        public QuickBooksService(IRequestFactory requestFactory)
        {
            _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
            Task.Run(() => UpdateCache());
            _logger.LogInfo("Updated cache in background");
            Task.Run(() => AutoUpdateCache(30));
        }

        public QuickBooksService()
        {
            _requestFactory = new RequestFactory();
            Task.Run(() => UpdateCache());
            _logger.LogInfo("Updated cache in background");
            Task.Run(() => AutoUpdateCache(30));
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
                UpdateCache();
                _logger.LogInfo("Updated cache in main thread");
            }

            _logger.LogTransaction("Retrieved all items from cache");
            return _cache["AllItemInventory"];
        }

        private void UpdateCache()
        {
            var req = _requestFactory.CreateAllItemNonInvQueryRequest();
            var result = req.SendRequest();

            lock (_cache)
            {
                _cache["AllItemInventory"] = result;
                _cacheValid = true;
            }
        }

        private void AutoUpdateCache(int min_interval)
        {
            int ms_interval = min_interval * 60 * 1000;
            while (true)
            {
                Thread.Sleep(ms_interval);
                UpdateCache();
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
            Task.Run(() => UpdateCache());
            _logger.LogInfo("Updated cache in background");

            return response;
        }
    }
}
