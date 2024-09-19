using Interop.QBFC14;
using QuickBooksIPCContracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;

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
        protected T1 _value;
        protected Connection _connection = new Connection();
        protected bool _open = false;
        protected IMsgSetRequest _msgSetRequest;

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

            _msgSetRequest = _connection.SessionManager.CreateMsgSetRequest("US", 14, 0);
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

        public T2 SendRequest()
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

    public class CustomerQueryResponse
    {
        public string Name { get; private set; }
        public string AccountNum { get; private set; }
        public CustomerQueryResponse(string name, string accountNum)
        {
            Name = name;
            AccountNum = accountNum;
        }
    }
    public class CustomerQueryRequest : Request<string, QBCustomer>
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

        protected override CustomerQueryResponse ConvertResponse(IMsgSetResponse responseSet)
        {
            IResponse response = GetFirstResponse(responseSet);
            if ((ENResponseType)response.Type.GetValue() != ENResponseType.rtCustomerQueryRs) { throw new QBRequestLibraryRuntimeError("Not a customerQueryResponse"); }
            ICustomerRetList customerRetList = (ICustomerRetList)response.Detail;
            ICustomerRet customerRet = (ICustomerRet)customerRetList.GetAt(0);
            string name = customerRet.FullName.GetValue();
            string num = customerRet.AccountNumber.GetValue();

            return new CustomerQueryResponse(name, num);
        }
    }

    public class SalesOrder
    {
        public string CustomerFullName { get; set; }
        public string CustomerPO { get; set; }
        public List<NonInvItem> SalesOrderLines { get; set; }

        public SalesOrder(string customerFullName, string PO, DateTime dueDate, List<NonInvItem> items)
        {
            SalesOrderLines = new List<NonInvItem>(items);
            CustomerFullName = customerFullName;
            CustomerPO = PO;
            DueDate = dueDate;
        }

        public void AddSalesOrderRq(ref ISalesOrderAdd SalesOrderAddRq)
        {
            SalesOrderAddRq.CustomerRef.FullName.SetValue(CustomerFullName);
            SalesOrderAddRq.PONumber.SetValue(CustomerPO);
            SalesOrderAddRq.DueDate.SetValue(DueDate);
            SalesOrderAddRq.ShipDate.SetValue(DueDate);

            foreach (var item in SalesOrderLines)
            {
                ISalesOrderLineAdd SalesOrderLineAdd = SalesOrderAddRq.ORSalesOrderLineAddList.Append().SalesOrderLineAdd;
                SalesOrderLineAdd.ItemRef.FullName.SetValue(item.Number);
                SalesOrderLineAdd.Quantity.SetValue(item.Quantity);
                SalesOrderLineAdd.ORRatePriceLevel.Rate.SetValue(item.Rate);
            }
        }
    }
    public class SalesOrderRequest : Request<SalesOrder, StatusResponse>
    {
        public SalesOrderRequest(SalesOrder salesOrder)
        {
            Set(salesOrder);
        }

        protected override void BuildHelper()
        {
            ISalesOrderAdd SalesOrderAddRq = _msgSetRequest.AppendSalesOrderAddRq();

            _value.AddSalesOrderRq(ref SalesOrderAddRq);
        }

        protected override StatusResponse ConvertResponse(IMsgSetResponse responseSet)
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
            return new StatusResponse(response.StatusMessage, response.StatusCode);
        }
    }

    public class AddItemNonInventoryRequest : Request<List<NonInvItem>, List<StatusResponse>>
    {
        public AddItemNonInventoryRequest(List<NonInvItem> nonInvItems)
        {
            Set(nonInvItems);
        }

        protected override void BuildHelper()
        {
            foreach (NonInvItem item in _value)
            {
                IItemNonInventoryAdd addRq = _msgSetRequest.AppendItemNonInventoryAddRq();
                addRq.Name.SetValue(item.Number);
                addRq.ORSalesPurchase.SalesOrPurchase.Desc.SetValue(item.Description);
                addRq.ORSalesPurchase.SalesOrPurchase.AccountRef.FullName.SetValue(item.AccountName);
            }
        }

        protected override List<StatusResponse> ConvertResponse(IMsgSetResponse responseSet)
        {
            List<StatusResponse> responseList = new List<StatusResponse>();
            IResponseList iResponseList = (IResponseList)responseSet.ResponseList;
            for (int i = 0; i < iResponseList.Count; ++i)
            {
                IResponse response = iResponseList.GetAt(i);
                if ((ENResponseType)response.Type.GetValue() != ENResponseType.rtItemNonInventoryAddRs) { throw new QBRequestLibraryRuntimeError("Not a NonInvItemAddResponse"); }
                responseList.Add(new StatusResponse(response.StatusMessage, response.StatusCode));
            }

            return responseList;
        }
    }

    /**
	 * <summary>Default Response when information other than success unnecessary.</summary>
	 */
    public class StatusResponse
    {
        public int _code;
        public string _message;
        public StatusResponse(string message, int code)
        {
            this._message = message;
            _code = code;
        }
    }

    public class NonInvItem : QBItem
    {
        public string AccountName { get; set; }
    }

    /**
	 * <summary>Represents SalesOrderRequest parameter</summary>
	 */
    
}
