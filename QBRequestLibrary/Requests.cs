using Interop.QBFC14;
using QuickBooksIPCContracts;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace QBRequestLibrary
{
    public interface IRequest<T1, T2>
    {
        T2 SendRequest();
    }

    /**
	 * <summary>Request class, abstract. Instantiate using concrete classes provided below.</summary>
	 * <remarks><c>Connect()</c> to open a connection and session, <c>Send()</c> to send request and receive response.</remarks>
	 */
    public abstract class Request<T1, T2> : IRequest<T1, T2>
    {
        Logger Logger = new Logger();
        protected T1 _value;
        protected Connection _connection = new Connection();
        protected bool _open = false;
        protected IMsgSetRequest _msgSetRequest;
        protected const short QBSDKMajorVersion = 14;
        protected const short QBSDKMinorVersion = 0;

        public virtual void Set(T1 value)
        {
            _value = value;
        }
        public virtual T1 Parameter()
        {
            return _value;
        }
        protected virtual void Connect(string file = "")
        {
            _connection.File = file;
            _connection.Open();
            _open = true;
        }

        protected virtual void Disconnect()
        {
            _connection.Close();
            _open = false;
        }

        protected abstract void BuildHelper();

        protected virtual void BuildRequest()
        {
            if (_value == null)
            {
                throw new NoValueException();
            }

            _msgSetRequest = _connection.SessionManager.CreateMsgSetRequest("US", QBSDKMajorVersion, QBSDKMinorVersion);
            _msgSetRequest.Attributes.OnError = ENRqOnError.roeContinue;
            BuildHelper();
        }

        protected virtual IResponse GetFirstResponse(IMsgSetResponse responseMsgSet)
        {
            if (responseMsgSet == null) throw new InvalidResponseException("No responseMsgSet");

            IResponseList responseList = responseMsgSet.ResponseList;
            if (responseList == null) throw new InvalidResponseException("No responseList");
            if (responseList.Count < 1) throw new InvalidResponseException("No response in responseList");

            IResponse response = responseList.GetAt(0);
            if (response.StatusCode < 0) throw new InvalidResponseException($"Negative statusCode: {response.StatusCode}");
            if (response.Detail == null) throw new InvalidResponseException("No response.Detail");

            return response;
        }

        protected abstract T2 ConvertResponse(IMsgSetResponse response);

        protected virtual T2 Send()
        {
            if (!_open)
            {
                throw new QBRequestLibraryRuntimeError("Connection Not Open");
            }
            BuildRequest();
            IMsgSetResponse response = _connection.SessionManager.DoRequests(_msgSetRequest);
            return ConvertResponse(response);
        }

        public virtual T2 SendRequest()
        {
            T2 response;
            Connect();
            try
            {
                response = Send();
            }
            catch (Exception e)
            {
                Disconnect();
                Logger.LogError(e.Message);
                throw e;
            }
            Disconnect();
            return response;

        }

        ~Request()
        {
            Disconnect();
        }
    }
    public class CustomerQueryRequest : Request<string, QBCustomer>, ICustomerQueryRequest
    {
        public CustomerQueryRequest(string name)
        {
            Set(name);
        }

        protected override void BuildHelper()
        {
            ICustomerQuery CustomerQueryRq = _msgSetRequest.AppendCustomerQueryRq();
            CustomerQueryRq.ORCustomerListQuery.CustomerListFilter.ORNameFilter.NameFilter.MatchCriterion.SetValue(ENMatchCriterion.mcEndsWith);
            CustomerQueryRq.ORCustomerListQuery.CustomerListFilter.ORNameFilter.NameFilter.Name.SetValue(_value);
        }

        protected override QBCustomer ConvertResponse(IMsgSetResponse responseSet)
        {
            IResponse response = GetFirstResponse(responseSet);
            if ((ENResponseType)response.Type.GetValue() != ENResponseType.rtCustomerQueryRs) { throw new QBRequestLibraryRuntimeError("Not a customerQueryResponse"); }
            ICustomerRetList customerRetList = (ICustomerRetList)response.Detail;
            ICustomerRet customerRet = (ICustomerRet)customerRetList.GetAt(0);
            string name = customerRet.FullName.GetValue();
            string num = customerRet.AccountNumber.GetValue();

            return new QBCustomer
            {
                AccountNumber = num,
                Name = name
            };
        }
    }

    public class SalesOrderRequest : Request<QBOrder, QBStatusResponse<string>>, ISalesOrderRequest
    {
        public SalesOrderRequest(QBOrder salesOrder)
        {
            Set(salesOrder);
        }

        protected override void BuildHelper()
        {
            ISalesOrderAdd SalesOrderAddRq = _msgSetRequest.AppendSalesOrderAddRq();

            SalesOrderAddRq.CustomerRef.FullName.SetValue(_value.Customer.Name);
            SalesOrderAddRq.PONumber.SetValue(_value.Customer.PO);
            SalesOrderAddRq.DueDate.SetValue(_value.DueDate);
            SalesOrderAddRq.ShipDate.SetValue(_value.DueDate);

            foreach (var item in _value.Items)
            {
                ISalesOrderLineAdd SalesOrderLineAdd = SalesOrderAddRq.ORSalesOrderLineAddList.Append().SalesOrderLineAdd;
                SalesOrderLineAdd.ItemRef.FullName.SetValue(item.Number);
                SalesOrderLineAdd.Desc.SetValue(item.Description);
                SalesOrderLineAdd.Quantity.SetValue(item.Quantity);
                SalesOrderLineAdd.ORRatePriceLevel.Rate.SetValue(item.Rate);
            }
        }

        protected override QBStatusResponse<string> ConvertResponse(IMsgSetResponse responseSet)
        {
            IResponse response;
            try
            {
                response = GetFirstResponse(responseSet);
            }
            catch (InvalidResponseException e)
            {
                Debug.WriteLine(string.Format("Exception caught: {0}", e.Message));
                response = responseSet.ResponseList.GetAt(0);
            }
            if ((ENResponseType)response.Type.GetValue() != ENResponseType.rtSalesOrderAddRs) { throw new QBRequestLibraryRuntimeError("Not a SalesOrderAddResponse"); }
            return new QBStatusResponse<string>
            {
                StatusCode = response.StatusCode,
                StatusMessage = response.StatusMessage

            };
        }
    }

    public class AddItemNonInventoryRequest : Request<List<QBItem>, List<QBStatusResponse<string>>>, IAddItemNonInventoryRequest
    {
        public AddItemNonInventoryRequest(List<QBItem> nonInvItems)
        {
            Set(nonInvItems);
        }

        protected override void BuildHelper()
        {
            foreach (QBItem item in _value)
            {
                IItemNonInventoryAdd addRq = _msgSetRequest.AppendItemNonInventoryAddRq();
                addRq.Name.SetValue(item.Number);
                addRq.ORSalesPurchase.SalesOrPurchase.Desc.SetValue(item.Description);
                addRq.ORSalesPurchase.SalesOrPurchase.AccountRef.FullName.SetValue(item.AccountName);
            }
        }

        protected override List<QBStatusResponse<string>> ConvertResponse(IMsgSetResponse responseSet)
        {
            List<QBStatusResponse<string>> responseList = new List<QBStatusResponse<string>>();
            IResponseList iResponseList = (IResponseList)responseSet.ResponseList;
            for (int i = 0; i < iResponseList.Count; ++i)
            {
                IResponse response = iResponseList.GetAt(i);
                if ((ENResponseType)response.Type.GetValue() != ENResponseType.rtItemNonInventoryAddRs) { throw new QBRequestLibraryRuntimeError("Not a NonInvItemAddResponse"); }
                responseList.Add(new QBStatusResponse<string>
                {
                    StatusCode = response.StatusCode,
                    StatusMessage = response.StatusMessage
                });
            }

            return responseList;
        }
    }

    public class AllItemNonInvQueryRequest : Request<object, QBStatusResponse<List<QBItem>>>, IAllItemNonInvQueryRequest
    {

        public AllItemNonInvQueryRequest()
        {
            Set(null);
        }
        protected override void BuildRequest()
        {
            _msgSetRequest = _connection.SessionManager.CreateMsgSetRequest("US", QBSDKMajorVersion, QBSDKMinorVersion);
            _msgSetRequest.Attributes.OnError = ENRqOnError.roeContinue;
            BuildHelper();
        }

        protected override void BuildHelper()
        {
            IItemNonInventoryQuery ItemNonInventoryQueryRq = _msgSetRequest.AppendItemNonInventoryQueryRq();
            // Specify only the necessary fields to retrieve
            ItemNonInventoryQueryRq.IncludeRetElementList.Add("Name");
            ItemNonInventoryQueryRq.IncludeRetElementList.Add("SalesOrPurchase");
            ItemNonInventoryQueryRq.IncludeRetElementList.Add("SalesAndPurchase");
        }

        protected override QBStatusResponse<List<QBItem>> ConvertResponse(IMsgSetResponse responseSet)
        {
            QBStatusResponse<List<QBItem>> retResponse = new QBStatusResponse<List<QBItem>>
            {
                Data = new List<QBItem>()
            };

            IResponseList responseList = responseSet.ResponseList;
            if (responseList == null || responseList.Count == 0)
            {
                throw new InvalidResponseException("No responses received.");
            }

            for (int i = 0; i < responseList.Count; ++i)
            {
                IResponse response = responseList.GetAt(i);
                if ((ENResponseType)response.Type.GetValue() != ENResponseType.rtItemNonInventoryQueryRs)
                {
                    throw new QBRequestLibraryRuntimeError("Unexpected response type.");
                }

                IItemNonInventoryRetList ItemNonInventoryRetList = response.Detail as IItemNonInventoryRetList;
                if (ItemNonInventoryRetList == null)
                {
                    continue; // Or handle as needed
                }

                for (int j = 0; j < ItemNonInventoryRetList.Count; ++j)
                {
                    IItemNonInventoryRet ItemRet = ItemNonInventoryRetList.GetAt(j);
                    QBItem item = new QBItem
                    {
                        Number = ItemRet.Name?.GetValue() ?? string.Empty,
                        Description = ItemRet.ORSalesPurchase?.SalesOrPurchase?.Desc?.GetValue()
                                      ?? ItemRet.ORSalesPurchase?.SalesAndPurchase?.SalesDesc?.GetValue()
                                      ?? string.Empty
                    };
                    retResponse.Data.Add(item);
                }
            }

            // Optionally, you can aggregate status codes and messages if needed
            // For simplicity, we're assuming a single status code/message
            if (responseList.Count > 0)
            {
                IResponse firstResponse = responseList.GetAt(0);
                retResponse.StatusCode = firstResponse.StatusCode;
                retResponse.StatusMessage = firstResponse.StatusMessage;
            }

            return retResponse;
        }
    }
} 