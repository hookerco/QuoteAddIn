// QuickBooksIPCService/QuickBooksService.cs
using System;
using System.ServiceModel;
using System.Collections.Generic;
using QBRequestLibrary;
using QuickBooksIPCContracts;
using QuoteItemResolution;
using System.Data.SqlClient;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;

namespace QuickBooksIPCService
{
    public sealed class CommercialTermsSnapshot
    {
        public List<string> CreditTerms { get; set; }
        public List<string> ShippingMethods { get; set; }
        public DateTime RefreshedAtUtc { get; set; }
    }

    public sealed class CommercialTermsCache
    {
        private readonly object _sync = new object();
        private CommercialTermsSnapshot _snapshot;

        public bool TryReplace(
            int statusCode,
            IEnumerable<string> creditTerms,
            IEnumerable<string> shippingMethods,
            DateTime refreshedAtUtc)
        {
            if (statusCode != 0 || creditTerms == null || shippingMethods == null)
            {
                return false;
            }

            var next = new CommercialTermsSnapshot
            {
                CreditTerms = new List<string>(creditTerms),
                ShippingMethods = new List<string>(shippingMethods),
                RefreshedAtUtc = refreshedAtUtc.ToUniversalTime()
            };
            lock (_sync)
            {
                _snapshot = next;
            }
            return true;
        }

        public CommercialTermsSnapshot Read()
        {
            lock (_sync)
            {
                if (_snapshot == null) return null;
                return new CommercialTermsSnapshot
                {
                    CreditTerms = new List<string>(_snapshot.CreditTerms),
                    ShippingMethods = new List<string>(_snapshot.ShippingMethods),
                    RefreshedAtUtc = _snapshot.RefreshedAtUtc
                };
            }
        }
    }

    public sealed class CustomerCommercialTermsCache
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, QBCustomerCommercialTerms> _entries =
            new Dictionary<string, QBCustomerCommercialTerms>(StringComparer.OrdinalIgnoreCase);

        public bool TryReplaceAll(
            int statusCode,
            IEnumerable<QBCustomerCommercialTermsRecord> records)
        {
            if (statusCode != 0 || records == null)
            {
                return false;
            }

            var next = new Dictionary<string, QBCustomerCommercialTerms>(
                StringComparer.OrdinalIgnoreCase);
            foreach (QBCustomerCommercialTermsRecord record in records)
            {
                string key = Normalize(record?.AccountNumber);
                if (key.Length == 0) continue;
                next[key] = new QBCustomerCommercialTerms
                {
                    CreditTerms = record.CreditTerms
                };
            }
            lock (_sync)
            {
                _entries.Clear();
                foreach (KeyValuePair<string, QBCustomerCommercialTerms> entry in next)
                {
                    _entries[entry.Key] = entry.Value;
                }
            }
            return true;
        }

        public QBCustomerCommercialTerms Read(string accountNumber)
        {
            string key = Normalize(accountNumber);
            lock (_sync)
            {
                QBCustomerCommercialTerms terms;
                return _entries.TryGetValue(key, out terms) ? Copy(terms) : null;
            }
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static QBCustomerCommercialTerms Copy(QBCustomerCommercialTerms source)
        {
            return new QBCustomerCommercialTerms
            {
                CreditTerms = source.CreditTerms
            };
        }
    }

    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    public class QuickBooksService : IQuickBooksService
    {
        private readonly IRequestFactory _requestFactory;
        private static readonly Dictionary<string, QBStatusResponse<List<QBItem>>> _cache
            = new Dictionary<string, QBStatusResponse<List<QBItem>>>();
        private bool _cacheValid = false;
        private readonly CommercialTermsCache _commercialTermsCache = new CommercialTermsCache();
        private readonly CustomerCommercialTermsCache _customerCommercialTermsCache =
            new CustomerCommercialTermsCache();
        private readonly Logger _logger;
        private readonly Func<string> _currentCompanyFileName;
        private bool _disposed = false;

        public QuickBooksService(IRequestFactory requestFactory)
            : this(requestFactory, new Logger(), CurrentCompanyFileName, initialize: true)
        {
        }

        public QuickBooksService(IRequestFactory requestFactory, Logger logger, bool initialize = true)
            : this(requestFactory, logger, CurrentCompanyFileName, initialize)
        {
        }

        public QuickBooksService(
            IRequestFactory requestFactory,
            Logger logger,
            Func<string> currentCompanyFileName,
            bool initialize = true)
        {
            _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _currentCompanyFileName = currentCompanyFileName ??
                throw new ArgumentNullException(nameof(currentCompanyFileName));
            if (initialize)
            {
                _initialize();
            }
        }

        public QuickBooksService()
            : this(new RequestFactory(), new Logger(), CurrentCompanyFileName, initialize: true)
        {
        }

        private static string CurrentCompanyFileName()
        {
            var connection = new Connection();
            bool opened = false;
            try
            {
                opened = connection.Open();
                return connection.CurrentCompanyFileName;
            }
            finally
            {
                try
                {
                    if (opened)
                    {
                        connection.Close();
                    }
                }
                finally
                {
                    GC.SuppressFinalize(connection);
                }
            }
        }

        public static string FingerprintCompanyFileName(string companyFileName)
        {
            return Connection.FingerprintCompanyFileName(companyFileName);
        }

        private void _initialize()
        {
            _logger.LogSessionStart();
            Task.Run(() =>
            {
                UpdateCache(background: true, initial: true);
                UpdateCommercialTermsCaches();
            });
            Task.Run(() => AutoUpdateCache(60));
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    _logger.LogSessionEnd();
                }
                // Dispose unmanaged resources if any
                _disposed = true;
            }
        }

        ~QuickBooksService()
        {
            Dispose(false);
        }

        public string Ping()
        {
            return "Pong";
        }

        public QBStatusResponse<QBCommercialTermsCatalog> GetCommercialTerms()
        {
            CommercialTermsSnapshot snapshot = _commercialTermsCache.Read();
            if (snapshot == null)
            {
                return new QBStatusResponse<QBCommercialTermsCatalog>
                {
                    StatusCode = 1,
                    StatusMessage = "QuickBooks commercial terms are unavailable.",
                    Data = null
                };
            }

            return new QBStatusResponse<QBCommercialTermsCatalog>
            {
                StatusCode = 0,
                StatusMessage = "OK",
                Data = new QBCommercialTermsCatalog
                {
                    CreditTerms = snapshot.CreditTerms,
                    ShippingMethods = snapshot.ShippingMethods,
                    RefreshedAtUtc = snapshot.RefreshedAtUtc
                }
            };
        }

        public QBStatusResponse<QBCustomerCommercialTerms> GetCustomerCommercialTerms(
            string accountNumber,
            bool refresh)
        {
            string key = (accountNumber ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                return CustomerCommercialTermsUnavailable();
            }

            QBCustomerCommercialTerms cached = _customerCommercialTermsCache.Read(key);

            if (cached == null)
            {
                return CustomerCommercialTermsUnavailable();
            }

            return new QBStatusResponse<QBCustomerCommercialTerms>
            {
                StatusCode = 0,
                StatusMessage = "OK",
                Data = cached
            };
        }

        private static QBStatusResponse<QBCustomerCommercialTerms>
            CustomerCommercialTermsUnavailable()
        {
            return new QBStatusResponse<QBCustomerCommercialTerms>
            {
                StatusCode = 1,
                StatusMessage = "QuickBooks customer commercial terms are unavailable.",
                Data = null
            };
        }

        private bool UpdateCommercialTermsCache()
        {
            try
            {
                ICommercialTermsQueryRequest request =
                    _requestFactory.CreateCommercialTermsQueryRequest();
                QBStatusResponse<QBCommercialTermsCatalog> response = request?.SendRequest();
                QBCommercialTermsCatalog catalog = response?.Data;
                return catalog != null && _commercialTermsCache.TryReplace(
                    response.StatusCode,
                    catalog.CreditTerms,
                    catalog.ShippingMethods,
                    catalog.RefreshedAtUtc);
            }
            catch
            {
                _logger.LogError("QuickBooks commercial terms refresh failed.");
                return false;
            }
        }

        private bool UpdateCustomerCommercialTermsCache()
        {
            try
            {
                ICustomerCommercialTermsQueryRequest request =
                    _requestFactory.CreateCustomerCommercialTermsQueryRequest();
                QBStatusResponse<List<QBCustomerCommercialTermsRecord>> response =
                    request?.SendRequest();
                return response != null && _customerCommercialTermsCache.TryReplaceAll(
                    response.StatusCode,
                    response.Data);
            }
            catch
            {
                _logger.LogError("QuickBooks customer commercial terms refresh failed.");
                return false;
            }
        }

        private void UpdateCommercialTermsCaches()
        {
            UpdateCommercialTermsCache();
            UpdateCustomerCommercialTermsCache();
        }

        public QBStatusResponse<string> AddOrder(QBOrder order)
        {
            return LegacyTransactionResponse(AddOrderWithIdentity(order, null));
        }

        public QBStatusResponse<string> AddEstimate(QBOrder order)
        {
            return LegacyTransactionResponse(AddEstimateWithIdentity(order, null));
        }

        private QBStatusResponse<QBTransactionIdentity> AddOrderWithIdentity(
            QBOrder order,
            string approvedCompanyFingerprint)
        {
            var req = _requestFactory.CreateSalesOrderRequest(
                order,
                approvedCompanyFingerprint);
            var response = req.SendRequest();
            _logger.LogTransaction($"Added order {order.QuoteNumber} for customer {order.Customer.Name}");
            return response;
        }

        private QBStatusResponse<QBTransactionIdentity> AddEstimateWithIdentity(
            QBOrder order,
            string approvedCompanyFingerprint)
        {
            var req = _requestFactory.CreateEstimateRequest(
                order,
                approvedCompanyFingerprint);
            var response = req.SendRequest();
            _logger.LogTransaction($"Added estimate {order.QuoteNumber} for customer {order.Customer.Name}");
            return response;
        }

        private static QBStatusResponse<string> LegacyTransactionResponse(
            QBStatusResponse<QBTransactionIdentity> response)
        {
            return new QBStatusResponse<string>
            {
                StatusCode = response.StatusCode,
                StatusMessage = response.StatusMessage,
                Data = response.Data?.TransactionId
            };
        }

        public QBStatusResponse<List<QBEstimateReference>> GetEstimateReferences()
        {
            return _requestFactory.CreateEstimateReferenceQueryRequest(null).SendRequest();
        }

        public QBStatusResponse<string> GetCurrentCompanyFingerprint()
        {
            try
            {
                return new QBStatusResponse<string>
                {
                    StatusCode = 0,
                    StatusMessage = "OK",
                    Data = FingerprintCompanyFileName(_currentCompanyFileName())
                };
            }
            catch
            {
                _logger.LogError("QuickBooks company identity query failed.");
                return new QBStatusResponse<string>
                {
                    StatusCode = 1,
                    StatusMessage = "QuickBooks company identity is unavailable.",
                    Data = null
                };
            }
        }

        public QBStatusResponse<QBEstimateReference> GetEstimateReference(string reference)
        {
            QBStatusResponse<List<QBEstimateReference>> response =
                GetEstimateReferenceMatches(reference);
            return new QBStatusResponse<QBEstimateReference>
            {
                StatusCode = response.StatusCode,
                StatusMessage = response.StatusMessage,
                Data = response.Data == null || response.Data.Count == 0
                    ? null : response.Data[0]
            };
        }

        private QBStatusResponse<List<QBEstimateReference>> GetEstimateReferenceMatches(
            string reference)
        {
            return _requestFactory.CreateEstimateReferenceQueryRequest(reference).SendRequest();
        }

        public QBStatusResponse<QBQuoteUploadResult> SubmitQuote(QBQuoteUploadRequest request)
        {
            string validationError = ValidateQuoteUploadRequest(request);
            if (validationError != null)
            {
                return Failure(validationError);
            }

            string approvedWriteFingerprint;
            QBStatusResponse<QBQuoteUploadResult> preflight = PreflightQuoteUpload(
                request,
                out approvedWriteFingerprint);
            if (preflight != null)
            {
                return preflight;
            }

            QBCustomer customer = ResolveQuoteUploadCustomer(request);
            if (customer == null)
            {
                return Failure($"Customer not found for account number {request.CustomerAccountNumber}");
            }

            QBStatusResponse<List<QBItem>> itemResponse;
            try
            {
                itemResponse = GetAllItems();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to retrieve QuickBooks items: {ex.Message}");
                return Failure($"Failed to retrieve QuickBooks items: {ex.Message}");
            }

            if (itemResponse == null || itemResponse.StatusCode != 0 || itemResponse.Data == null)
            {
                return Failure(
                    itemResponse?.StatusMessage ?? "Failed to retrieve QuickBooks items",
                    itemResponse?.StatusCode ?? 1);
            }

            QuoteUploadItemResolution resolution = QuoteUploadItemResolver.Resolve(request.Lines, itemResponse.Data);
            if (resolution.ItemsToCreate.Count > 0)
            {
                List<QBStatusResponse<string>> addItemResponses;
                try
                {
                    addItemResponses = AddNonInvItem(
                        resolution.ItemsToCreate,
                        approvedWriteFingerprint);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to create QuickBooks items: {ex.Message}");
                    return Failure($"Failed to create QuickBooks items: {ex.Message}");
                }

                QBStatusResponse<string> failedAdd = FirstFailure(addItemResponses);
                if (failedAdd != null)
                {
                    return Failure(failedAdd.StatusMessage, failedAdd.StatusCode);
                }
            }

            QBOrder order = BuildOrder(request, customer, resolution.ResolvedLines);
            QBQuoteUploadResult result = new QBQuoteUploadResult
            {
                TransactionType = request.TransactionType,
                CustomerName = customer.Name,
                QuoteNumber = request.QuoteNumber,
                Lines = resolution.ResolvedLines
            };

            QBStatusResponse<QBTransactionIdentity> transactionResponse;
            try
            {
                transactionResponse = request.TransactionType == QBQuoteTransactionType.Estimate
                    ? AddEstimateWithIdentity(order, approvedWriteFingerprint)
                    : AddOrderWithIdentity(order, approvedWriteFingerprint);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to submit QuickBooks quote: {ex.Message}");
                return Failure($"Failed to submit QuickBooks quote: {ex.Message}");
            }

            if (transactionResponse.StatusCode == 0 && transactionResponse.Data != null)
            {
                result.TransactionId = transactionResponse.Data.TransactionId;
                result.AssignedReference = transactionResponse.Data.AssignedReference;
            }

            return new QBStatusResponse<QBQuoteUploadResult>
            {
                StatusCode = transactionResponse.StatusCode,
                StatusMessage = transactionResponse.StatusMessage,
                Data = transactionResponse.StatusCode == 0 ? result : null
            };
        }

        private QBStatusResponse<QBQuoteUploadResult> PreflightQuoteUpload(
            QBQuoteUploadRequest request,
            out string approvedWriteFingerprint)
        {
            approvedWriteFingerprint = null;
            if (request.QuoteKind == "test")
            {
                string fingerprint;
                try
                {
                    fingerprint = FingerprintCompanyFileName(_currentCompanyFileName());
                }
                catch
                {
                    _logger.LogError("Test quote company verification failed.");
                    return Failure("Test quote cannot be submitted to this QuickBooks company.");
                }

                if (!IsApprovedCompanyFingerprint(
                    request.ApprovedTestCompanyFingerprints,
                    fingerprint))
                {
                    _logger.LogError("Test quote company verification failed.");
                    return Failure("Test quote cannot be submitted to this QuickBooks company.");
                }

                approvedWriteFingerprint = fingerprint;
            }

            if (request.TransactionType != QBQuoteTransactionType.Estimate)
            {
                return null;
            }

            QBStatusResponse<List<QBEstimateReference>> references;
            try
            {
                references = approvedWriteFingerprint == null
                    ? GetEstimateReferenceMatches(request.QuoteNumber)
                    : _requestFactory.CreateEstimateReferenceQueryRequest(
                        request.QuoteNumber,
                        approvedWriteFingerprint).SendRequest();
            }
            catch
            {
                _logger.LogError("QuickBooks Estimate preflight failed.");
                return Failure("QuickBooks Estimate preflight is unavailable.");
            }

            if (references == null ||
                references.StatusCode != 0 ||
                references.Data == null)
            {
                _logger.LogError("QuickBooks Estimate preflight failed.");
                return Failure("QuickBooks Estimate preflight is unavailable.");
            }

            if (references.Data.Count == 0)
            {
                return null;
            }

            if (references.Data.Count == 1 &&
                !string.IsNullOrWhiteSpace(request.ConfirmedTransactionId) &&
                string.Equals(
                    request.ConfirmedTransactionId,
                    references.Data[0].TransactionId,
                    StringComparison.Ordinal))
            {
                return new QBStatusResponse<QBQuoteUploadResult>
                {
                    StatusCode = 0,
                    StatusMessage = "Estimate was already submitted.",
                    Data = new QBQuoteUploadResult
                    {
                        TransactionType = request.TransactionType,
                        QuoteNumber = request.QuoteNumber,
                        TransactionId = references.Data[0].TransactionId,
                        AssignedReference = request.QuoteNumber,
                        Lines = new List<QBQuoteUploadResolvedLine>()
                    }
                };
            }

            return Failure("QuickBooks Estimate reconciliation is required.");
        }

        private static bool IsApprovedCompanyFingerprint(
            List<string> approvedFingerprints,
            string currentFingerprint)
        {
            if (approvedFingerprints == null || approvedFingerprints.Count == 0)
            {
                return false;
            }

            bool matched = false;
            foreach (string approved in approvedFingerprints)
            {
                if (!IsSha256Fingerprint(approved))
                {
                    return false;
                }
                matched |= string.Equals(
                    approved,
                    currentFingerprint,
                    StringComparison.OrdinalIgnoreCase);
            }
            return matched;
        }

        private static bool IsSha256Fingerprint(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            foreach (char character in value)
            {
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f') ||
                      (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        public QBCustomer GetCustomer(string accountNumber)
        {
            var req = _requestFactory.CreateCustomerQueryRequest(accountNumber);
            var response = req.SendRequest();
            _logger.LogTransaction($"Retrieved customer {response.Name} with account number {response.AccountNumber}");
            return response;
        }

        private QBCustomer GetCustomerByAccountNumber(string accountNumber)
        {
            var req = _requestFactory.CreateCustomerAccountNumberQueryRequest(accountNumber);
            return req.SendRequest();
        }

        private string ValidateQuoteUploadRequest(QBQuoteUploadRequest request)
        {
            if (request == null)
            {
                return "Quote upload request is required";
            }

            if (request.TransactionType != QBQuoteTransactionType.Estimate &&
                request.TransactionType != QBQuoteTransactionType.SalesOrder)
            {
                return "Quote upload request TransactionType must be Estimate or SalesOrder";
            }

            if (request.QuoteKind != "normal" && request.QuoteKind != "test")
            {
                return "Quote upload request QuoteKind must be normal or test";
            }

            if (string.IsNullOrWhiteSpace(request.QuoteNumber))
            {
                return "Quote upload request requires a QuoteNumber";
            }

            if (!QuoteIdentityMatchesKind(request.QuoteKind, request.QuoteNumber))
            {
                return "Quote upload request QuoteNumber does not match QuoteKind";
            }

            if (request.TransactionType == QBQuoteTransactionType.SalesOrder &&
                request.DueDate == DateTime.MinValue)
            {
                return "SalesOrder quote upload request requires a DueDate";
            }

            if (request.Lines == null || request.Lines.Count == 0)
            {
                return "Quote upload request must include at least one line";
            }

            for (int i = 0; i < request.Lines.Count; ++i)
            {
                QBQuoteUploadLine line = request.Lines[i];
                if (line == null)
                {
                    return $"Quote upload line {i + 1} is required";
                }

                if (string.IsNullOrWhiteSpace(line.Description))
                {
                    return $"Quote upload line {i + 1} requires a description";
                }

                if (line.Quantity <= 0)
                {
                    return $"Quote upload line {i + 1} requires a positive quantity";
                }

                if (double.IsNaN(line.Rate) || double.IsInfinity(line.Rate))
                {
                    return $"Quote upload line {i + 1} has an invalid rate";
                }
            }

            if (request.Customer == null && string.IsNullOrWhiteSpace(request.CustomerAccountNumber))
            {
                return "Quote upload request requires a customer or customer account number";
            }

            return null;
        }

        private static bool QuoteIdentityMatchesKind(string quoteKind, string quoteNumber)
        {
            if (quoteKind == "normal")
            {
                if (quoteNumber.Length == 0 ||
                    quoteNumber.Length > 11 ||
                    quoteNumber.StartsWith("TEST-", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                bool anyLetterOrNonZeroDigit = false;
                foreach (char character in quoteNumber)
                {
                    bool digit = character >= '0' && character <= '9';
                    bool letter = (character >= 'A' && character <= 'Z') ||
                                  (character >= 'a' && character <= 'z');
                    if (!digit && !letter && character != '-')
                    {
                        return false;
                    }
                    anyLetterOrNonZeroDigit |= letter ||
                                               (digit && character != '0');
                }
                return anyLetterOrNonZeroDigit;
            }

            if (quoteKind != "test" ||
                quoteNumber.Length != 11 ||
                !quoteNumber.StartsWith("TEST-", StringComparison.Ordinal))
            {
                return false;
            }
            for (int index = 5; index < quoteNumber.Length; ++index)
            {
                char character = quoteNumber[index];
                if (!((character >= 'A' && character <= 'Z') ||
                      (character >= '2' && character <= '7')))
                {
                    return false;
                }
            }
            return true;
        }

        private QBCustomer ResolveQuoteUploadCustomer(QBQuoteUploadRequest request)
        {
            QBCustomer customer = request.Customer;
            if (customer == null)
            {
                try
                {
                    customer = GetCustomerByAccountNumber(request.CustomerAccountNumber);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to retrieve customer {request.CustomerAccountNumber}: {ex.Message}");
                    return null;
                }
            }

            if (customer == null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(customer.Name) && !string.IsNullOrWhiteSpace(request.CustomerName))
            {
                customer.Name = request.CustomerName;
            }

            if (!string.IsNullOrWhiteSpace(request.CustomerPO))
            {
                customer.PO = request.CustomerPO;
            }

            return string.IsNullOrWhiteSpace(customer.Name) ? null : customer;
        }

        private static QBOrder BuildOrder(
            QBQuoteUploadRequest request,
            QBCustomer customer,
            List<QBQuoteUploadResolvedLine> resolvedLines)
        {
            var items = new List<QBItem>();
            foreach (QBQuoteUploadResolvedLine line in resolvedLines)
            {
                items.Add(new QBItem
                {
                    Number = line.Number,
                    Description = line.Description,
                    Quantity = line.Quantity,
                    Rate = line.Rate
                });
            }

            return new QBOrder
            {
                QuoteNumber = request.QuoteNumber,
                Customer = customer,
                DueDate = request.DueDate,
                Items = items
            };
        }

        private static QBStatusResponse<string> FirstFailure(List<QBStatusResponse<string>> responses)
        {
            if (responses == null)
            {
                return new QBStatusResponse<string>
                {
                    StatusCode = 1,
                    StatusMessage = "No item creation response returned"
                };
            }

            foreach (QBStatusResponse<string> response in responses)
            {
                if (response == null)
                {
                    return new QBStatusResponse<string>
                    {
                        StatusCode = 1,
                        StatusMessage = "Item creation response was empty"
                    };
                }

                if (response.StatusCode != 0)
                {
                    return response;
                }
            }

            return null;
        }

        private static QBStatusResponse<QBQuoteUploadResult> Failure(string message, int statusCode = 1)
        {
            return new QBStatusResponse<QBQuoteUploadResult>
            {
                StatusCode = statusCode == 0 ? 1 : statusCode,
                StatusMessage = message,
                Data = null
            };
        }

        public QBStatusResponse<List<QBItem>> GetAllItems()
        {
            // Check if cache is still valid
            if (!_cacheValid)
            {
                // Perform the query (this is the long-running part)
                UpdateCache(background: false);
                _logger.LogInfo("Updated cache in main thread");
            }

            _logger.LogTransaction("Retrieved all items from cache");
            return _cache["AllItemInventory"];
        }

        private void UpdateCache(bool background, bool initial = false)
        {
            if (initial)
            {
                Console.WriteLine("Updating cache, service not ready");
            }
            var req = _requestFactory.CreateAllItemNonInvQueryRequest();

            Stopwatch sw = new Stopwatch();
            sw.Start();
            var result = req.SendRequest();
            sw.Stop();
            var elapsedSeconds = (double)sw.ElapsedMilliseconds / 1000;

            lock (_cache)
            {
                _cache["AllItemInventory"] = result;
                _cacheValid = true;
            }

            _logger.LogInfo($"Updated cache in {(background ? "background" : "main thread")}: {elapsedSeconds} seconds");
            if (initial)
            {
                Console.WriteLine($"WCF Service is running... {elapsedSeconds} second start-up");
            }
        }

        private void AutoUpdateCache(int min_interval)
        {
            int ms_interval = min_interval * 60 * 1000;
            while (true)
            {
                Thread.Sleep(ms_interval);
                UpdateCache(background: true);
                UpdateCommercialTermsCaches();
            }
        }

        public int InvalidateAllItemsCache()
        {
            _cacheValid = false;
            _logger.LogInfo("Invalidated cache");
            return 0;
        }

        public List<QBStatusResponse<string>> AddNonInvItem(List<QBItem> items)
        {
            return AddNonInvItem(items, null);
        }

        private List<QBStatusResponse<string>> AddNonInvItem(
            List<QBItem> items,
            string approvedCompanyFingerprint)
        {
            var req = _requestFactory.CreateAddItemNonInventoryRequest(
                items,
                approvedCompanyFingerprint);
            var response = req.SendRequest();
            foreach (var rs in response)
            {
                if (rs.StatusCode != 0)
                {
                    _logger.LogError($"Failed to add item {rs.Data} with error: {rs.StatusMessage}");
                }
            }

            InvalidateAllItemsCache();
            Task.Run(() => UpdateCache(background: true));
            _logger.LogInfo("Updated cache in background");

            return response;
        }
    }
}
