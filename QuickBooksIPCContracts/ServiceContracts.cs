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
        QBStatusResponse<string> AddEstimate(QBOrder order);

        [OperationContract]
        QBStatusResponse<QBQuoteUploadResult> SubmitQuote(QBQuoteUploadRequest request);

        [OperationContract]
        QBCustomer GetCustomer(string accountNumber);

        [OperationContract]
        QBStatusResponse<List<QBItem>> GetAllItems();

        [OperationContract]
        int InvalidateAllItemsCache();

        [OperationContract]
        List<QBStatusResponse<string>> AddNonInvItem(List<QBItem> item);
    }
}
