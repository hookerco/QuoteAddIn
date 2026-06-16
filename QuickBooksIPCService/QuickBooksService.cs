// QuickBooksIPCService/QuickBooksService.cs
using System;
using System.ServiceModel;
using System.Collections.Generic;
using QBRequestLibrary;
using QuickBooksIPCContracts;
using System.Data.SqlClient;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;

namespace QuickBooksIPCService
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    public class QuickBooksService : IQuickBooksService
    {
        private readonly IRequestFactory _requestFactory;
        private static readonly Dictionary<string, QBStatusResponse<List<QBItem>>> _cache
            = new Dictionary<string, QBStatusResponse<List<QBItem>>>();
        private bool _cacheValid = false;
        private readonly Logger _logger;
        private bool _disposed = false;

        public QuickBooksService(IRequestFactory requestFactory)
        {
            _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
            _logger = new Logger();
            _initialize();
        }

        public QuickBooksService(IRequestFactory requestFactory, Logger logger, bool initialize = true)
        {
            _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (initialize)
            {
                _initialize();
            }
        }

        public QuickBooksService()
            : this(new RequestFactory(), new Logger())
        {
        }

        private void _initialize()
        {
            _logger.LogSessionStart();
            Task.Run(() => UpdateCache(background: true, initial: true));
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

        public QBStatusResponse<string> AddOrder(QBOrder order)
        {
            var req = _requestFactory.CreateSalesOrderRequest(order);
            var response = req.SendRequest();
            _logger.LogTransaction($"Added order {order.QuoteNumber} for customer {order.Customer.Name}");
            return response;
        }

        public QBStatusResponse<string> AddEstimate(QBOrder order)
        {
            var req = _requestFactory.CreateEstimateRequest(order);
            var response = req.SendRequest();
            _logger.LogTransaction($"Added estimate {order.QuoteNumber} for customer {order.Customer.Name}");
            return response;
        }

        public QBStatusResponse<QBQuoteUploadResult> SubmitQuote(QBQuoteUploadRequest request)
        {
            string validationError = ValidateQuoteUploadRequest(request);
            if (validationError != null)
            {
                return Failure(validationError);
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
                    addItemResponses = AddNonInvItem(resolution.ItemsToCreate);
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

            QBStatusResponse<string> transactionResponse;
            try
            {
                transactionResponse = request.TransactionType == QBQuoteTransactionType.Estimate
                    ? AddEstimate(order)
                    : AddOrder(order);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to submit QuickBooks quote: {ex.Message}");
                return Failure($"Failed to submit QuickBooks quote: {ex.Message}");
            }

            return new QBStatusResponse<QBQuoteUploadResult>
            {
                StatusCode = transactionResponse.StatusCode,
                StatusMessage = transactionResponse.StatusMessage,
                Data = transactionResponse.StatusCode == 0 ? result : null
            };
        }

        public QBCustomer GetCustomer(string accountNumber)
        {
            var req = _requestFactory.CreateCustomerQueryRequest(accountNumber);
            var response = req.SendRequest();
            _logger.LogTransaction($"Retrieved customer {response.Name} with account number {response.AccountNumber}");
            return response;
        }

        private string ValidateQuoteUploadRequest(QBQuoteUploadRequest request)
        {
            if (request == null)
            {
                return "Quote upload request is required";
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

        private QBCustomer ResolveQuoteUploadCustomer(QBQuoteUploadRequest request)
        {
            QBCustomer customer = request.Customer;
            if (customer == null)
            {
                try
                {
                    customer = GetCustomer(request.CustomerAccountNumber);
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
            var req = _requestFactory.CreateAddItemNonInventoryRequest(items);
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
