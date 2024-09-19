using System.ServiceModel;
using QuickBooksIPCContracts;

namespace QuickBooksIPCService
{
    [ServiceContract]
    public interface IQuickBooksService
    {
        [OperationContract]
        QBStatusResponse<QBOrder> AddOrder(QBOrder order);

        [OperationContract]
        QBOrder GetOrder(string order);

        [OperationContract]
        QBCustomer GetCustomer(string accountNumber);

        [OperationContract]
        QBStatusResponse<QBItem> GetItem(QBItem item);

        [OperationContract]
        QBStatusResponse<QBItem> AddItem(QBItem item);
    }
}
