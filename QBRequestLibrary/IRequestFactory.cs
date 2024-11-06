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
        ISalesOrderRequest CreateSalesOrderRequest(QBOrder order);
        IEstimateRequest CreateEstimateRequest(QBOrder order);
        IAddItemNonInventoryRequest CreateAddItemNonInventoryRequest(List<QBItem> items);
        IAllItemNonInvQueryRequest CreateAllItemNonInvQueryRequest();
    }
}
