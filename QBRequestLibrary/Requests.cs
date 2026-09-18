using Interop.QBFC14;
using QuickBooksIPCContracts;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;

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
        private string _requiredCompanyFingerprint;
        private bool _ownsConnection = true;
        private long _buildMs, _queryMs, _convertMs;
        protected virtual bool LogReadTimings => false;

        internal void UseOpenConnection(Connection connection)
        {
            _connection = connection;
            _ownsConnection = false;
            _open = true;
        }

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

        protected void RequireCompanyFingerprint(string approvedCompanyFingerprint)
        {
            _requiredCompanyFingerprint = approvedCompanyFingerprint;
        }

        private void VerifyRequiredCompanyFingerprint()
        {
            if (string.IsNullOrWhiteSpace(_requiredCompanyFingerprint))
            {
                return;
            }

            string currentFingerprint;
            try
            {
                currentFingerprint = Connection.FingerprintCompanyFileName(
                    _connection.CurrentCompanyFileName);
            }
            catch
            {
                throw new QBRequestLibraryRuntimeError(
                    "QuickBooks company verification failed.");
            }

            if (!string.Equals(
                _requiredCompanyFingerprint,
                currentFingerprint,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new QBRequestLibraryRuntimeError(
                    "QuickBooks company verification failed.");
            }
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

        protected virtual string FailureLogMessage(Exception error)
        {
            return error.Message;
        }

        protected virtual T2 Send()
        {
            if (!_open)
            {
                throw new QBRequestLibraryRuntimeError("Connection Not Open");
            }
            var timer = Stopwatch.StartNew();
            BuildRequest();
            _buildMs = timer.ElapsedMilliseconds;
            timer.Restart();
            IMsgSetResponse response = _connection.SessionManager.DoRequests(_msgSetRequest);
            _queryMs = timer.ElapsedMilliseconds;
            timer.Restart();
            T2 result = ConvertResponse(response);
            _convertMs = timer.ElapsedMilliseconds;
            return result;
        }

        public virtual T2 SendRequest()
        {
            var timer = Stopwatch.StartNew();
            long connectMs = 0;
            _buildMs = _queryMs = _convertMs = 0;
            try
            {
                if (_ownsConnection) Connect();
                connectMs = timer.ElapsedMilliseconds;
                VerifyRequiredCompanyFingerprint();
                return Send();
            }
            catch (Exception e)
            {
                Logger.LogError(FailureLogMessage(e));
                throw;
            }
            finally
            {
                var closeTimer = Stopwatch.StartNew();
                try
                {
                    if (_ownsConnection && _open) Disconnect();
                }
                finally
                {
                    if (LogReadTimings)
                        Logger.LogInfo($"QB read {GetType().Name}: connect_ms={connectMs}; build_ms={_buildMs}; query_ms={_queryMs}; convert_ms={_convertMs}; close_ms={closeTimer.ElapsedMilliseconds}; total_ms={timer.ElapsedMilliseconds}; shared_session={!_ownsConnection}.");
                }
            }
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

    public class CustomerAccountNumberQueryRequest
        : Request<string, QBCustomer>, ICustomerAccountNumberQueryRequest
    {
        public CustomerAccountNumberQueryRequest(string accountNumber)
        {
            Set(accountNumber);
        }

        protected override void BuildHelper()
        {
            ICustomerQuery request = _msgSetRequest.AppendCustomerQueryRq();
            request.IncludeRetElementList.Add("AccountNumber");
            request.IncludeRetElementList.Add("FullName");
        }

        protected override QBCustomer ConvertResponse(IMsgSetResponse responseSet)
        {
            IResponse response = GetFirstResponse(responseSet);
            if ((ENResponseType)response.Type.GetValue() != ENResponseType.rtCustomerQueryRs)
            {
                throw new QBRequestLibraryRuntimeError("Not a customerQueryResponse");
            }

            ICustomerRetList customers = (ICustomerRetList)response.Detail;
            for (int index = 0; index < customers.Count; index++)
            {
                ICustomerRet customer = customers.GetAt(index);
                string accountNumber = customer.AccountNumber?.GetValue();
                if (string.Equals(
                    accountNumber?.Trim(),
                    _value?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return new QBCustomer
                    {
                        AccountNumber = accountNumber,
                        Name = customer.FullName.GetValue()
                    };
                }
            }

            return null;
        }
    }

    public class SalesOrderRequest : Request<QBOrder, QBStatusResponse<QBTransactionIdentity>>, ISalesOrderRequest
    {
        public SalesOrderRequest(QBOrder salesOrder)
            : this(salesOrder, null)
        {
        }

        public SalesOrderRequest(QBOrder salesOrder, string approvedCompanyFingerprint)
        {
            Set(salesOrder);
            RequireCompanyFingerprint(approvedCompanyFingerprint);
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

        protected override QBStatusResponse<QBTransactionIdentity> ConvertResponse(IMsgSetResponse responseSet)
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
            ISalesOrderRet salesOrderRet = response.Detail as ISalesOrderRet;
            return new QBStatusResponse<QBTransactionIdentity>
            {
                StatusCode = response.StatusCode,
                StatusMessage = response.StatusMessage,
                Data = salesOrderRet == null ? null : new QBTransactionIdentity
                {
                    TransactionId = salesOrderRet.TxnID?.GetValue(),
                    AssignedReference = salesOrderRet.RefNumber?.GetValue()
                }
            };
        }
    }

    public class EstimateRequest : Request<QBOrder, QBStatusResponse<QBTransactionIdentity>>, IEstimateRequest
    {
        public EstimateRequest(QBOrder estimate)
            : this(estimate, null)
        {
        }

        public EstimateRequest(QBOrder estimate, string approvedCompanyFingerprint)
        {
            Set(estimate);
            RequireCompanyFingerprint(approvedCompanyFingerprint);
        }

        protected override void BuildHelper()
        {
            IEstimateAdd EstimateAddRq = _msgSetRequest.AppendEstimateAddRq();

            EstimateAddRq.CustomerRef.FullName.SetValue(_value.Customer.Name);
            EstimateAddRq.PONumber.SetValue(_value.Customer.PO);
            if (!string.IsNullOrWhiteSpace(_value.QuoteNumber))
            {
                EstimateAddRq.RefNumber.SetValue(_value.QuoteNumber);
            }
            EstimateAddRq.TxnDate.SetValue(DateTime.Today);

            foreach (var item in _value.Items)
            {
                IEstimateLineAdd EstimateLineAdd = EstimateAddRq.OREstimateLineAddList.Append().EstimateLineAdd;
                EstimateLineAdd.ItemRef.FullName.SetValue(item.Number);
                EstimateLineAdd.Desc.SetValue(item.Description);
                EstimateLineAdd.Quantity.SetValue(item.Quantity);
                EstimateLineAdd.ORRate.Rate.SetValue(item.Rate);
            }
        }

        protected override QBStatusResponse<QBTransactionIdentity> ConvertResponse(IMsgSetResponse responseSet)
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
            if ((ENResponseType)response.Type.GetValue() != ENResponseType.rtEstimateAddRs) { throw new QBRequestLibraryRuntimeError("Not an EstimateAddResponse"); }
            IEstimateRet estimateRet = response.Detail as IEstimateRet;
            return new QBStatusResponse<QBTransactionIdentity>
            {
                StatusCode = response.StatusCode,
                StatusMessage = response.StatusMessage,
                Data = estimateRet == null ? null : new QBTransactionIdentity
                {
                    TransactionId = estimateRet.TxnID?.GetValue(),
                    AssignedReference = estimateRet.RefNumber?.GetValue()
                }
            };
        }
    }

    public class EstimateReferenceQueryRequest
        : Request<string, QBStatusResponse<List<QBEstimateReference>>>,
          IEstimateReferenceQueryRequest
    {
        public EstimateReferenceQueryRequest(string reference)
            : this(reference, null)
        {
        }

        public EstimateReferenceQueryRequest(
            string reference,
            string approvedCompanyFingerprint)
        {
            Set(reference ?? string.Empty);
            RequireCompanyFingerprint(approvedCompanyFingerprint);
        }

        protected override string FailureLogMessage(Exception error)
        {
            return "QuickBooks Estimate reference query failed.";
        }

        protected override void BuildHelper()
        {
            IEstimateQuery request = _msgSetRequest.AppendEstimateQueryRq();
            request.IncludeRetElementList.Add("TxnID");
            request.IncludeRetElementList.Add("RefNumber");

            if (!string.IsNullOrWhiteSpace(_value))
            {
                request.ORTxnQuery.RefNumberList.Add(_value);
            }
        }

        protected override QBStatusResponse<List<QBEstimateReference>> ConvertResponse(
            IMsgSetResponse responseSet)
        {
            IResponse response;
            try
            {
                response = GetFirstResponse(responseSet);
            }
            catch (InvalidResponseException)
            {
                response = responseSet.ResponseList.GetAt(0);
            }

            if ((ENResponseType)response.Type.GetValue() != ENResponseType.rtEstimateQueryRs)
            {
                throw new QBRequestLibraryRuntimeError("Not an EstimateQueryResponse");
            }

            bool numericScan = string.IsNullOrWhiteSpace(_value);
            var references = new List<QBEstimateReference>();
            IEstimateRetList estimates = response.Detail as IEstimateRetList;
            // SDK 1 is no match; 500 is an object-not-found response. This query
            // names exactly one RefNumber, so an empty 500 means that estimate
            // is absent. Never accept 500 for an unfiltered scan or partial data.
            if ((response.StatusCode == 1 || (response.StatusCode == 500 && !numericScan)) &&
                (response.Detail == null || (estimates != null && estimates.Count == 0)))
            {
                return new QBStatusResponse<List<QBEstimateReference>>
                {
                    StatusCode = 0,
                    StatusMessage = "No matching estimates found.",
                    Data = references
                };
            }
            for (int index = 0; estimates != null && index < estimates.Count; index++)
            {
                IEstimateRet estimate = estimates.GetAt(index);
                string reference = estimate.RefNumber?.GetValue();
                long numericReference;
                if (string.IsNullOrEmpty(reference) ||
                    (numericScan &&
                     (!long.TryParse(
                         reference,
                         NumberStyles.None,
                         CultureInfo.InvariantCulture,
                         out numericReference) || numericReference <= 0)) ||
                    (!numericScan && !string.Equals(reference, _value, StringComparison.Ordinal)))
                {
                    continue;
                }

                references.Add(new QBEstimateReference
                {
                    Reference = reference,
                    TransactionId = estimate.TxnID?.GetValue()
                });
            }

            return new QBStatusResponse<List<QBEstimateReference>>
            {
                StatusCode = response.StatusCode,
                StatusMessage = response.StatusMessage,
                Data = references
            };
        }
    }

    public class AddItemNonInventoryRequest : Request<List<QBItem>, List<QBStatusResponse<string>>>, IAddItemNonInventoryRequest
    {
        public AddItemNonInventoryRequest(List<QBItem> nonInvItems)
            : this(nonInvItems, null)
        {
        }

        public AddItemNonInventoryRequest(
            List<QBItem> nonInvItems,
            string approvedCompanyFingerprint)
        {
            Set(nonInvItems);
            RequireCompanyFingerprint(approvedCompanyFingerprint);
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
        protected override bool LogReadTimings => true;

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
            AppendNonInventoryQuery();
            AppendServiceQuery();
        }

        private void AppendNonInventoryQuery()
        {
            IItemNonInventoryQuery rq = _msgSetRequest.AppendItemNonInventoryQueryRq();
            rq.IncludeRetElementList.Add("Name");
            rq.IncludeRetElementList.Add("SalesOrPurchase");
            rq.IncludeRetElementList.Add("SalesAndPurchase");
            rq.IncludeRetElementList.Add("IsActive");
            rq.ORListQueryWithOwnerIDAndClass.ListWithClassFilter.ActiveStatus.SetValue(ENActiveStatus.asAll);
        }

        private void AppendServiceQuery()
        {
            // Query ItemService as well so callers get a complete item lookup list.
            IItemServiceQuery rq = _msgSetRequest.AppendItemServiceQueryRq();
            rq.IncludeRetElementList.Add("Name");
            rq.IncludeRetElementList.Add("SalesOrPurchase");
            rq.IncludeRetElementList.Add("SalesAndPurchase");
            rq.IncludeRetElementList.Add("IsActive");
            rq.ORListQueryWithOwnerIDAndClass.ListWithClassFilter.ActiveStatus.SetValue(ENActiveStatus.asAll);
        }

        protected override QBStatusResponse<List<QBItem>> ConvertResponse(IMsgSetResponse responseSet)
        {
            return CacheResponseReader.Items(responseSet?.ToXMLString());
        }
    }
    public class CommercialTermsQueryRequest
        : Request<object, QBStatusResponse<QBCommercialTermsCatalog>>,
          ICommercialTermsQueryRequest
    {
        protected override bool LogReadTimings => true;
        public CommercialTermsQueryRequest()
        {
            Set(null);
        }

        protected override void BuildRequest()
        {
            _msgSetRequest = _connection.SessionManager.CreateMsgSetRequest(
                "US", QBSDKMajorVersion, QBSDKMinorVersion);
            _msgSetRequest.Attributes.OnError = ENRqOnError.roeContinue;
            BuildHelper();
        }

        protected override void BuildHelper()
        {
            ITermsQuery terms = _msgSetRequest.AppendTermsQueryRq();
            terms.IncludeRetElementList.Add("Name");
            terms.ORListQuery.ListFilter.ActiveStatus.SetValue(ENActiveStatus.asActiveOnly);

            IShipMethodQuery shipping = _msgSetRequest.AppendShipMethodQueryRq();
            shipping.IncludeRetElementList.Add("Name");
            shipping.ORListQuery.ListFilter.ActiveStatus.SetValue(ENActiveStatus.asActiveOnly);
        }

        protected override QBStatusResponse<QBCommercialTermsCatalog> ConvertResponse(IMsgSetResponse responseSet)
        {
            return CacheResponseReader.Terms(responseSet?.ToXMLString());
        }
    }
    public class CustomerCommercialTermsQueryRequest
        : Request<object, QBStatusResponse<List<QBCustomerCommercialTermsRecord>>>,
          ICustomerCommercialTermsQueryRequest
    {
        protected override bool LogReadTimings => true;
        public CustomerCommercialTermsQueryRequest()
        {
            Set(null);
        }

        protected override void BuildRequest()
        {
            _msgSetRequest = _connection.SessionManager.CreateMsgSetRequest(
                "US", QBSDKMajorVersion, QBSDKMinorVersion);
            _msgSetRequest.Attributes.OnError = ENRqOnError.roeContinue;
            BuildHelper();
        }

        protected override void BuildHelper()
        {
            ICustomerQuery customers = _msgSetRequest.AppendCustomerQueryRq();
            customers.IncludeRetElementList.Add("AccountNumber");
            customers.IncludeRetElementList.Add("TermsRef");
            customers.IncludeRetElementList.Add("DataExtRet");
            customers.OwnerIDList.Add("0");
        }

        protected override QBStatusResponse<List<QBCustomerCommercialTermsRecord>> ConvertResponse(IMsgSetResponse responseSet)
        {
            return CacheResponseReader.Customers(responseSet?.ToXMLString());
        }
    }
}