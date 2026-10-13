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

        // A distinct operation makes an older service reject, not ignore, discounts.
        [OperationContract]
        QBStatusResponse<QBQuoteUploadResult> SubmitDiscountedQuote(QBQuoteUploadRequest request);

        [OperationContract]
        QBStatusResponse<List<QBEstimateReference>> GetEstimateReferences();

        [OperationContract]
        QBStatusResponse<QBEstimateReference> GetEstimateReference(string reference);

        [OperationContract]
        QBStatusResponse<string> GetCurrentCompanyFingerprint();

        [OperationContract]
        QBStatusResponse<QBCommercialTermsCatalog> GetCommercialTerms();

        [OperationContract]
        QBStatusResponse<QBCustomerCommercialTerms> GetCustomerCommercialTerms(
            string accountNumber,
            bool refresh);

        [OperationContract]
        QBCustomer GetCustomer(string accountNumber);

        [OperationContract]
        QBStatusResponse<List<QBItem>> GetAllItems();

        [OperationContract]
        int InvalidateAllItemsCache();

        [OperationContract]
        QBStatusResponse<string> RefreshQuickBooksCaches();

        [OperationContract]
        List<QBStatusResponse<string>> AddNonInvItem(List<QBItem> item);
    }
}
