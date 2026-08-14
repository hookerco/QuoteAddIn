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

        public ISalesOrderRequest CreateSalesOrderRequest(
            QBOrder order,
            string approvedCompanyFingerprint)
        {
            return new SalesOrderRequest(order, approvedCompanyFingerprint);
        }

        public IEstimateRequest CreateEstimateRequest(QBOrder order)
        {
            return new EstimateRequest(order);
        }

        public IEstimateRequest CreateEstimateRequest(
            QBOrder order,
            string approvedCompanyFingerprint)
        {
            return new EstimateRequest(order, approvedCompanyFingerprint);
        }

        public IEstimateReferenceQueryRequest CreateEstimateReferenceQueryRequest(string reference)
        {
            return new EstimateReferenceQueryRequest(reference);
        }

        public IAddItemNonInventoryRequest CreateAddItemNonInventoryRequest(List<QBItem> items)
        {
            return new AddItemNonInventoryRequest(items);
        }

        public IAddItemNonInventoryRequest CreateAddItemNonInventoryRequest(
            List<QBItem> items,
            string approvedCompanyFingerprint)
        {
            return new AddItemNonInventoryRequest(items, approvedCompanyFingerprint);
        }

        public IAllItemNonInvQueryRequest CreateAllItemNonInvQueryRequest()
        {
            return new AllItemNonInvQueryRequest();
        }

        public ICommercialTermsQueryRequest CreateCommercialTermsQueryRequest()
        {
            return new CommercialTermsQueryRequest();
        }

        public ICustomerCommercialTermsQueryRequest CreateCustomerCommercialTermsQueryRequest()
        {
            return new CustomerCommercialTermsQueryRequest();
        }
    }
}

