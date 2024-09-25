// QuickBooksIPCService/QuickBooksService.cs
using System;
using System.Collections.Generic;
using QBRequestLibrary;
using QuickBooksIPCContracts;

namespace QuickBooksIPCService
{
    public class QuickBooksService : IQuickBooksService
    {
        private readonly IRequestFactory _requestFactory;

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
            var req = _requestFactory.CreateAllItemNonInvQueryRequest();
            return req.SendRequest();
        }

        public List<QBStatusResponse<string>> AddNonInvItem(List<QBItem> items)
        {
            var req = _requestFactory.CreateAddItemNonInventoryRequest(items);
            return req.SendRequest();
        }
    }
}
