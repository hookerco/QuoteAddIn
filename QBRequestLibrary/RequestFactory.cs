using QuickBooksIPCContracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// QBRequestLibrary/RequestFactory.cs
namespace QBRequestLibrary
{
    public class RequestFactory : IRequestFactory
    {
        public ICustomerQueryRequest CreateCustomerQueryRequest(string accountNumber)
        {
            return new CustomerQueryRequest(accountNumber);
        }

        public ICustomerAccountNumberQueryRequest CreateCustomerAccountNumberQueryRequest(string accountNumber)
        {
            return new CustomerAccountNumberQueryRequest(accountNumber);
        }

        public ISalesOrderRequest CreateSalesOrderRequest(QBOrder order)
        {
            return new SalesOrderRequest(order);
        }

        public IEstimateRequest CreateEstimateRequest(QBOrder order)
        {
            return new EstimateRequest(order);
        }

        public IAddItemNonInventoryRequest CreateAddItemNonInventoryRequest(List<QBItem> items)
        {
            return new AddItemNonInventoryRequest(items);
        }

        public IAllItemNonInvQueryRequest CreateAllItemNonInvQueryRequest()
        {
            return new AllItemNonInvQueryRequest();
        }

        public ICommercialTermsQueryRequest CreateCommercialTermsQueryRequest()
        {
            return new CommercialTermsQueryRequest();
        }

        public ICustomerCommercialTermsQueryRequest CreateCustomerCommercialTermsQueryRequest(
            string accountNumber)
        {
            return new CustomerCommercialTermsQueryRequest(accountNumber);
        }
    }
}

