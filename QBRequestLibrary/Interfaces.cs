using QuickBooksIPCContracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// QBRequestLibrary/Interfaces.cs
namespace QBRequestLibrary
{
    public interface ICustomerQueryRequest : IRequest<string, QBCustomer> { }

    public interface ICustomerAccountNumberQueryRequest : IRequest<string, QBCustomer> { }

    public interface ISalesOrderRequest : IRequest<QBOrder, QBStatusResponse<QBTransactionIdentity>> { }

    public interface IEstimateRequest : IRequest<QBOrder, QBStatusResponse<QBTransactionIdentity>> { }

    public interface IEstimateReferenceQueryRequest
        : IRequest<string, QBStatusResponse<List<QBEstimateReference>>> { }

    public interface IAddItemNonInventoryRequest : IRequest<List<QBItem>, List<QBStatusResponse<string>>> { }

    public interface IAllItemNonInvQueryRequest : IRequest<object, QBStatusResponse<List<QBItem>>> { }

    public interface ICommercialTermsQueryRequest
        : IRequest<object, QBStatusResponse<QBCommercialTermsCatalog>> { }

    public interface ICustomerCommercialTermsQueryRequest
        : IRequest<object, QBStatusResponse<List<QBCustomerCommercialTermsRecord>>> { }
}

