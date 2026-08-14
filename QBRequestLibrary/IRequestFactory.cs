using QuickBooksIPCContracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QBRequestLibrary
{
    public interface IRequestFactory
    {
        ICustomerQueryRequest CreateCustomerQueryRequest(string accountNumber);
        ICustomerAccountNumberQueryRequest CreateCustomerAccountNumberQueryRequest(string accountNumber);
        ISalesOrderRequest CreateSalesOrderRequest(QBOrder order);
        ISalesOrderRequest CreateSalesOrderRequest(QBOrder order, string approvedCompanyFingerprint);
        IEstimateRequest CreateEstimateRequest(QBOrder order);
        IEstimateRequest CreateEstimateRequest(QBOrder order, string approvedCompanyFingerprint);
        IEstimateReferenceQueryRequest CreateEstimateReferenceQueryRequest(string reference);
        IAddItemNonInventoryRequest CreateAddItemNonInventoryRequest(List<QBItem> items);
        IAddItemNonInventoryRequest CreateAddItemNonInventoryRequest(
            List<QBItem> items,
            string approvedCompanyFingerprint);
        IAllItemNonInvQueryRequest CreateAllItemNonInvQueryRequest();
        ICommercialTermsQueryRequest CreateCommercialTermsQueryRequest();
        ICustomerCommercialTermsQueryRequest CreateCustomerCommercialTermsQueryRequest();
    }
}
