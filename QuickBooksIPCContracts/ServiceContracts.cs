using System.ServiceModel;
using QuickBooksIPCContracts;

namespace QuickBooksIPCService
{
    [ServiceContract]
    public interface IQuickBooksService
    {
        [OperationContract]
        StatusResponse<QBOrder> AddOrder(QBOrder order);

        [OperationContract]
        QBOrder GetOrder(string order);

        [OperationContract]
        QBCustomer GetCustomer(string accountNumber);
    }
}
