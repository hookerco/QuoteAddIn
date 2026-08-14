using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;
using QuickBooksIPCContracts;

namespace QuickBooksConnectorCore
{
    /// <summary>
    /// Produces the bounded workstation identity and Estimate-reference payloads used by
    /// Quote Module administration. Raw company filenames and QuickBooks status messages
    /// never enter the serialized response.
    /// </summary>
    public static class QuoteNumberAdminHandler
    {
        private static readonly JavaScriptSerializer JsonSerializer = new JavaScriptSerializer();

        public static string HandleReconciliation()
        {
            try
            {
                using (var connection = new QuickBooksServiceConnection())
                {
                    return HandleReconciliation(
                        connection.Client.GetCurrentCompanyFingerprint,
                        connection.Client.GetEstimateReferences,
                        Environment.UserName);
                }
            }
            catch
            {
                throw ReconciliationUnavailable();
            }
        }

        public static string HandleReconciliation(
            Func<QBStatusResponse<string>> fetchCompanyFingerprint,
            Func<QBStatusResponse<List<QBEstimateReference>>> fetchEstimateReferences,
            string windowsUsername)
        {
            try
            {
                string fingerprint = ReadFingerprint(fetchCompanyFingerprint?.Invoke());
                QBStatusResponse<List<QBEstimateReference>> response =
                    fetchEstimateReferences?.Invoke();
                if (response == null || response.StatusCode != 0 || response.Data == null)
                {
                    throw ReconciliationUnavailable();
                }
                if (!string.Equals(
                    fingerprint,
                    ReadFingerprint(fetchCompanyFingerprint?.Invoke()),
                    StringComparison.Ordinal))
                {
                    throw ReconciliationUnavailable();
                }

                var estimates = new List<Dictionary<string, object>>();
                foreach (QBEstimateReference estimate in response.Data)
                {
                    if (estimate == null || !IsPositiveDecimal(estimate.Reference))
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(estimate.TransactionId))
                    {
                        throw ReconciliationUnavailable();
                    }
                    estimates.Add(new Dictionary<string, object>
                    {
                        { "reference", estimate.Reference },
                        { "transaction_id", estimate.TransactionId.Trim() }
                    });
                }

                return JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    { "schema_version", 1 },
                    { "windows_username", windowsUsername ?? string.Empty },
                    { "company_fingerprint", fingerprint },
                    { "estimates", estimates }
                });
            }
            catch
            {
                throw ReconciliationUnavailable();
            }
        }

        public static string HandleCompanyIdentity()
        {
            try
            {
                using (var connection = new QuickBooksServiceConnection())
                {
                    return HandleCompanyIdentity(
                        connection.Client.GetCurrentCompanyFingerprint,
                        Environment.UserName);
                }
            }
            catch
            {
                throw CompanyIdentityUnavailable();
            }
        }

        public static string HandleCompanyIdentity(
            Func<QBStatusResponse<string>> fetchCompanyFingerprint,
            string windowsUsername)
        {
            try
            {
                return JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    { "schema_version", 1 },
                    { "windows_username", windowsUsername ?? string.Empty },
                    { "company_fingerprint", ReadFingerprint(fetchCompanyFingerprint?.Invoke()) }
                });
            }
            catch
            {
                throw CompanyIdentityUnavailable();
            }
        }

        private static string ReadFingerprint(QBStatusResponse<string> response)
        {
            string fingerprint = response?.Data?.Trim();
            if (response == null || response.StatusCode != 0 || !IsSha256Fingerprint(fingerprint))
            {
                throw CompanyIdentityUnavailable();
            }
            return fingerprint.ToLowerInvariant();
        }

        private static bool IsSha256Fingerprint(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsPositiveDecimal(string value)
        {
            long number;
            return long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out number) && number > 0;
        }

        private static InvalidOperationException ReconciliationUnavailable()
        {
            return new InvalidOperationException(
                "QuickBooks quote-number reconciliation is unavailable.");
        }

        private static InvalidOperationException CompanyIdentityUnavailable()
        {
            return new InvalidOperationException(
                "QuickBooks company identity is unavailable.");
        }
    }
}
