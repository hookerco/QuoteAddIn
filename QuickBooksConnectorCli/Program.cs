using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.ServiceModel;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;
using QuickBooksIPCContracts;

namespace QuickBooksConnectorCli
{
    internal static class Program
    {
        private const string ServiceBaseAddress = "net.pipe://localhost/QuickBooksService";

        private static readonly JavaScriptSerializer JsonSerializer = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 100
        };

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    throw new CliUsageException("Operation is required. Supported operations: ping, submit-quote.");
                }

                string operation = args[0].Trim().ToLowerInvariant();
                switch (operation)
                {
                    case "ping":
                        return RunPing();

                    case "submit-quote":
                        return RunSubmitQuote();

                    default:
                        throw new CliUsageException($"Unsupported operation '{args[0]}'. Supported operations: ping, submit-quote.");
                }
            }
            catch (CliUsageException ex)
            {
                Console.Error.WriteLine(ex.Message);
                WriteErrorJson(2, ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                WriteErrorJson(1, ex.Message);
                return 1;
            }
        }

        private static int RunPing()
        {
            using (var connection = new QuickBooksServiceConnection())
            {
                string reply = connection.Client.Ping();
                WriteJson(new Dictionary<string, object>
                {
                    { "status", "ok" },
                    { "reply", reply }
                });
            }

            return 0;
        }

        private static int RunSubmitQuote()
        {
            string input = Console.In.ReadToEnd();
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new CliUsageException("submit-quote requires a QBQuoteUploadRequest JSON payload on stdin.");
            }

            QBQuoteUploadRequest request = ParseQuoteUploadRequest(input);
            QBStatusResponse<QBQuoteUploadResult> response;
            using (var connection = new QuickBooksServiceConnection())
            {
                response = connection.Client.SubmitQuote(request);
            }

            WriteJson(ToJsonResponse(response));
            return 0;
        }

        private static QBQuoteUploadRequest ParseQuoteUploadRequest(string json)
        {
            object raw = JsonSerializer.DeserializeObject(json);
            var root = AsDictionary(raw, "request");
            var request = new QBQuoteUploadRequest
            {
                TransactionType = ParseTransactionType(GetValue(root, "TransactionType", "transactionType", "transaction_type")),
                QuoteNumber = GetString(root, "QuoteNumber", "quoteNumber", "quote_number"),
                CustomerAccountNumber = GetString(root, "CustomerAccountNumber", "customerAccountNumber", "customer_account_number"),
                CustomerName = GetString(root, "CustomerName", "customerName", "customer_name"),
                CustomerPO = GetString(root, "CustomerPO", "customerPO", "customerPo", "customer_po"),
                DueDate = GetDateTime(root, "DueDate", "dueDate", "due_date"),
                Customer = ParseCustomer(GetValue(root, "Customer", "customer")),
                Lines = ParseLines(GetValue(root, "Lines", "lines"))
            };

            return request;
        }

        private static QBCustomer ParseCustomer(object value)
        {
            if (value == null)
            {
                return null;
            }

            var customer = AsDictionary(value, "customer");
            return new QBCustomer
            {
                Name = GetString(customer, "Name", "name"),
                AccountNumber = GetString(customer, "AccountNumber", "accountNumber", "account_number"),
                PO = GetString(customer, "PO", "po")
            };
        }

        private static List<QBQuoteUploadLine> ParseLines(object value)
        {
            if (value == null)
            {
                return null;
            }

            var lines = new List<QBQuoteUploadLine>();
            object[] lineValues = value as object[];
            if (lineValues == null)
            {
                throw new CliUsageException("Lines must be a JSON array.");
            }

            for (int i = 0; i < lineValues.Length; ++i)
            {
                var line = AsDictionary(lineValues[i], $"lines[{i}]");
                lines.Add(new QBQuoteUploadLine
                {
                    Description = GetString(line, "Description", "description"),
                    Quantity = GetInt(line, "Quantity", "quantity"),
                    Rate = GetDouble(line, "Rate", "rate"),
                    OverrideNumber = GetString(line, "OverrideNumber", "overrideNumber", "override_number")
                });
            }

            return lines;
        }

        private static QBQuoteTransactionType ParseTransactionType(object value)
        {
            if (value == null)
            {
                return QBQuoteTransactionType.Estimate;
            }

            if (value is int)
            {
                int numeric = (int)value;
                if (numeric == 0)
                {
                    return QBQuoteTransactionType.Estimate;
                }

                if (numeric == 1)
                {
                    return QBQuoteTransactionType.SalesOrder;
                }
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            string normalized = NormalizeKey(text);
            if (normalized == "estimate")
            {
                return QBQuoteTransactionType.Estimate;
            }

            if (normalized == "salesorder")
            {
                return QBQuoteTransactionType.SalesOrder;
            }

            throw new CliUsageException($"Unsupported TransactionType '{text}'. Use Estimate or SalesOrder.");
        }

        private static Dictionary<string, object> AsDictionary(object value, string path)
        {
            var dictionary = value as Dictionary<string, object>;
            if (dictionary == null)
            {
                throw new CliUsageException($"{path} must be a JSON object.");
            }

            return dictionary;
        }

        private static object GetValue(Dictionary<string, object> dictionary, params string[] names)
        {
            foreach (string name in names)
            {
                string normalizedName = NormalizeKey(name);
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (NormalizeKey(pair.Key) == normalizedName)
                    {
                        return pair.Value;
                    }
                }
            }

            return null;
        }

        private static string GetString(Dictionary<string, object> dictionary, params string[] names)
        {
            object value = GetValue(dictionary, names);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int GetInt(Dictionary<string, object> dictionary, params string[] names)
        {
            object value = GetValue(dictionary, names);
            return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static double GetDouble(Dictionary<string, object> dictionary, params string[] names)
        {
            object value = GetValue(dictionary, names);
            return value == null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static DateTime GetDateTime(Dictionary<string, object> dictionary, params string[] names)
        {
            object value = GetValue(dictionary, names);
            if (value == null)
            {
                return default(DateTime);
            }

            if (value is DateTime)
            {
                return (DateTime)value;
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
            {
                return default(DateTime);
            }

            Match legacyMatch = Regex.Match(text, @"^/Date\((?<milliseconds>-?\d+)");
            if (legacyMatch.Success)
            {
                long milliseconds = long.Parse(legacyMatch.Groups["milliseconds"].Value, CultureInfo.InvariantCulture);
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddMilliseconds(milliseconds)
                    .ToLocalTime();
            }

            return DateTime.Parse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal);
        }

        private static object ToJsonResponse(QBStatusResponse<QBQuoteUploadResult> response)
        {
            if (response == null)
            {
                return new Dictionary<string, object>
                {
                    { "StatusCode", 1 },
                    { "StatusMessage", "No response returned from QuickBooks service" },
                    { "Data", null }
                };
            }

            return new Dictionary<string, object>
            {
                { "StatusCode", response.StatusCode },
                { "StatusMessage", response.StatusMessage },
                { "Data", ToJsonResult(response.Data) }
            };
        }

        private static object ToJsonResult(QBQuoteUploadResult result)
        {
            if (result == null)
            {
                return null;
            }

            var lines = new List<Dictionary<string, object>>();
            foreach (QBQuoteUploadResolvedLine line in result.Lines ?? new List<QBQuoteUploadResolvedLine>())
            {
                lines.Add(new Dictionary<string, object>
                {
                    { "Number", line.Number },
                    { "Description", line.Description },
                    { "Quantity", line.Quantity },
                    { "Rate", line.Rate },
                    { "CreatedItem", line.CreatedItem }
                });
            }

            return new Dictionary<string, object>
            {
                { "TransactionType", result.TransactionType.ToString() },
                { "CustomerName", result.CustomerName },
                { "QuoteNumber", result.QuoteNumber },
                { "Lines", lines }
            };
        }

        private static string NormalizeKey(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return Regex.Replace(value, @"[\s_\-]", string.Empty).ToLowerInvariant();
        }

        private static void WriteJson(object value)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(value));
        }

        private static void WriteErrorJson(int statusCode, string statusMessage)
        {
            WriteJson(new Dictionary<string, object>
            {
                { "StatusCode", statusCode == 0 ? 1 : statusCode },
                { "StatusMessage", statusMessage },
                { "Data", null }
            });
        }

        private sealed class QuickBooksServiceConnection : IDisposable
        {
            private static readonly TimeSpan ServiceOperationTimeout = TimeSpan.FromMinutes(5);
            private readonly ChannelFactory<IQuickBooksService> _channelFactory;

            internal QuickBooksServiceConnection()
            {
                var binding = new NetNamedPipeBinding
                {
                    OpenTimeout = TimeSpan.FromSeconds(30),
                    CloseTimeout = TimeSpan.FromSeconds(30),
                    SendTimeout = ServiceOperationTimeout,
                    ReceiveTimeout = ServiceOperationTimeout,
                    MaxReceivedMessageSize = int.MaxValue,
                    ReaderQuotas = new XmlDictionaryReaderQuotas
                    {
                        MaxDepth = 32,
                        MaxStringContentLength = int.MaxValue,
                        MaxArrayLength = int.MaxValue,
                        MaxBytesPerRead = 4096,
                        MaxNameTableCharCount = int.MaxValue
                    }
                };

                _channelFactory = new ChannelFactory<IQuickBooksService>(binding, new EndpointAddress(ServiceBaseAddress));
                Client = _channelFactory.CreateChannel();
                ((IContextChannel)Client).OperationTimeout = ServiceOperationTimeout;
            }

            internal IQuickBooksService Client { get; private set; }

            public void Dispose()
            {
                CloseCommunicationObject(Client as ICommunicationObject);
                CloseCommunicationObject(_channelFactory);
            }

            private static void CloseCommunicationObject(ICommunicationObject communicationObject)
            {
                if (communicationObject == null)
                {
                    return;
                }

                try
                {
                    if (communicationObject.State != CommunicationState.Closed)
                    {
                        communicationObject.Close();
                    }
                }
                catch
                {
                    communicationObject.Abort();
                }
            }
        }

        private class CliUsageException : Exception
        {
            internal CliUsageException(string message)
                : base(message)
            {
            }
        }
    }
}
