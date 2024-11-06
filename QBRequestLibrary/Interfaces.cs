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

    public interface ISalesOrderRequest : IRequest<QBOrder, QBStatusResponse<string>> { }

    public interface IEstimateRequest : IRequest<QBOrder, QBStatusResponse<string>> { }

    public interface IAddItemNonInventoryRequest : IRequest<List<QBItem>, List<QBStatusResponse<string>>> { }

    public interface IAllItemNonInvQueryRequest : IRequest<object, QBStatusResponse<List<QBItem>>> { }
}

