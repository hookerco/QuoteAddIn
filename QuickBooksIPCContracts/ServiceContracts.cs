using System.Collections.Generic;
using System.ServiceModel;

namespace QuickBooksIPCContracts
{
    [ServiceContract]
    public interface IQuickBooksService
    {
        [OperationContract]
        string Ping();

        [OperationContract]
        QBStatusResponse<string> AddOrder(QBOrder order);

        [OperationContract]
        QBCustomer GetCustomer(string accountNumber);

        [OperationContract]
        QBStatusResponse<List<QBItem>> GetAllItems();

        [OperationContract]
        List<QBStatusResponse<string>> AddNonInvItem(List<QBItem> item);
    }
}
