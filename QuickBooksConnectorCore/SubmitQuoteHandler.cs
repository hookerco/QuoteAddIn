using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using QuickBooksIPCContracts;

namespace QuickBooksConnectorCore
{
    /// <summary>
    /// The single submit-quote code path shared by the connector CLI (stdin/stdout) and
    /// the localhost bridge (HTTP). Parses a <c>to_connector_payload()</c> JSON body into a
    /// <see cref="QBQuoteUploadRequest"/>, submits it over the NetNamedPipe, and returns the
    /// bare <c>{StatusCode, StatusMessage, Data}</c> response JSON. Keeping this in one place
    /// is what stops the CLI and bridge from drifting.
    /// </summary>
    public static class SubmitQuoteHandler
    {
        private static readonly JavaScriptSerializer JsonSerializer = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 100
        };

        /// <summary>
        /// Production path: parse the payload, submit it over the live NetNamedPipe service,
        /// and return the serialized bare response.
        /// </summary>
        public static string Handle(string json)
        {
            return Handle(json, SubmitOverPipe);
        }

        /// <summary>
        /// Parse <paramref name="json"/>, hand the request to <paramref name="submit"/>, and
        /// serialize the bare <c>{StatusCode, StatusMessage, Data}</c> response. The
        /// <paramref name="submit"/> seam lets callers (and tests) substitute the transport
        /// without re-implementing the parse/serialize contract.
        /// </summary>
        public static string Handle(string json, Func<QBQuoteUploadRequest, QBStatusResponse<QBQuoteUploadResult>> submit)
        {
            if (submit == null)
            {
                throw new ArgumentNullException(nameof(submit));
            }

            QBQuoteUploadRequest request = ParseQuoteUploadRequest(json);
            QBStatusResponse<QBQuoteUploadResult> response = submit(request);
            return JsonSerializer.Serialize(ToJsonResponse(response));
        }

        private static QBStatusResponse<QBQuoteUploadResult> SubmitOverPipe(QBQuoteUploadRequest request)
        {
            using (var connection = new QuickBooksServiceConnection())
            {
                return connection.Client.SubmitQuote(request);
            }
        }

        public static QBQuoteUploadRequest ParseQuoteUploadRequest(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new QuoteRequestParseException("A QBQuoteUploadRequest JSON payload is required.");
            }

            object raw;
            try
            {
                raw = JsonSerializer.DeserializeObject(json);
            }
            catch (Exception ex) when (!(ex is QuoteRequestParseException))
            {
                throw new QuoteRequestParseException("Request body is not valid JSON: " + ex.Message);
            }

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
                throw new QuoteRequestParseException("Lines must be a JSON array.");
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

            throw new QuoteRequestParseException($"Unsupported TransactionType '{text}'. Use Estimate or SalesOrder.");
        }

        private static Dictionary<string, object> AsDictionary(object value, string path)
        {
            var dictionary = value as Dictionary<string, object>;
            if (dictionary == null)
            {
                throw new QuoteRequestParseException($"{path} must be a JSON object.");
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
    }
}
