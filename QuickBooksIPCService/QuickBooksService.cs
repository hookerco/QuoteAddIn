// QuickBooksIPCService/QuickBooksService.cs
using System;
using System.ServiceModel;
using System.Collections.Generic;
using QBRequestLibrary;
using QuickBooksIPCContracts;

namespace QuickBooksIPCService
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    public class QuickBooksService : IQuickBooksService
    {
        private readonly IRequestFactory _requestFactory;
        private static readonly Dictionary<string, QBStatusResponse<List<QBItem>>> _cache
            = new Dictionary<string, QBStatusResponse<List<QBItem>>>();
        private bool _cacheValid = false;

        public QuickBooksService(IRequestFactory requestFactory)
        {
            _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
        }

        public QuickBooksService()
        {
            _requestFactory = new RequestFactory();
        }

        public string Ping()
        {
            return "Pong";
        }

        public QBStatusResponse<string> AddOrder(QBOrder order)
        {
            var req = _requestFactory.CreateSalesOrderRequest(order);
            return req.SendRequest();
        }

        public QBCustomer GetCustomer(string accountNumber)
        {
            var req = _requestFactory.CreateCustomerQueryRequest(accountNumber);
            return req.SendRequest();
        }

        public QBStatusResponse<List<QBItem>> GetAllItems()
        {
            // Check if cache is still valid
            if (_cacheValid)
            {
                return _cache["AllItemInventory"];
            }

            // Perform the query (this is the long-running part)
            var req = _requestFactory.CreateAllItemNonInvQueryRequest();
            var result = req.SendRequest();

            // Update cache
            _cache["AllItemInventory"] = result;
            _cacheValid = true;

            return result;
        }

        public int InvalidateAllItemsCache()
        {
            _cacheValid = false;
            return 0;
        }

        public List<QBStatusResponse<string>> AddNonInvItem(List<QBItem> items)
        {
            var req = _requestFactory.CreateAddItemNonInventoryRequest(items);
            return req.SendRequest();
        }
    }
}
