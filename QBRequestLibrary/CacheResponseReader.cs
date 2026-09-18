using QuickBooksIPCContracts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace QBRequestLibrary
{
    // Transfer the response once, then traverse managed XML instead of thousands
    // of cross-process QBFC properties. Never log or persist the response XML.
    internal static class CacheResponseReader
    {
        private static List<XElement> Responses(string xml, params string[] expected)
        {
            if (string.IsNullOrWhiteSpace(xml)) throw new InvalidResponseException("No responses received.");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(text, settings))
            {
                var root = XDocument.Load(reader).Root;
                var responses = root?.Name == "QBXML"
                    ? root.Element("QBXMLMsgsRs")?.Elements().ToList() : null;
                if (responses == null || responses.Count != expected.Length ||
                    expected.Any(name => responses.Count(response => response.Name == name) != 1))
                    throw new InvalidResponseException("Unexpected or incomplete QuickBooks cache response.");
                return responses;
            }
        }

        private static int Status(XElement response)
        {
            int code;
            if (!int.TryParse((string)response.Attribute("statusCode"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out code))
                throw new InvalidResponseException("Missing QuickBooks response status.");
            return code;
        }

        private static void SetStatus<T>(QBStatusResponse<T> result, XElement response)
        {
            int code = Status(response);
            // SDK status 1 is a successful empty query, not a failed refresh.
            bool empty = !response.Elements().Any();
            if (code == 1 && empty) code = 0;
            if (result.StatusMessage == null || code != 0)
            {
                result.StatusCode = code;
                result.StatusMessage = (string)response.Attribute("statusMessage") ?? string.Empty;
            }
        }

        public static QBStatusResponse<List<QBItem>> Items(string xml)
        {
            var result = new QBStatusResponse<List<QBItem>> { Data = new List<QBItem>() };
            foreach (var response in Responses(xml, "ItemNonInventoryQueryRs", "ItemServiceQueryRs"))
            {
                SetStatus(result, response);
                if (Status(response) != 0) continue;
                string itemName = response.Name == "ItemNonInventoryQueryRs" ? "ItemNonInventoryRet" : "ItemServiceRet";
                foreach (var item in response.Elements(itemName))
                    result.Data.Add(new QBItem
                    {
                        Number = (string)item.Element("Name") ?? string.Empty,
                        Description = (string)item.Element("SalesOrPurchase")?.Element("Desc")
                            ?? (string)item.Element("SalesAndPurchase")?.Element("SalesDesc") ?? string.Empty,
                        Active = (bool?)item.Element("IsActive") ?? false
                    });
            }
            if (result.StatusCode != 0) result.Data = null;
            return result;
        }

        public static QBStatusResponse<QBCommercialTermsCatalog> Terms(string xml)
        {
            var result = new QBStatusResponse<QBCommercialTermsCatalog>
            {
                Data = new QBCommercialTermsCatalog
                {
                    CreditTerms = new List<string>(), ShippingMethods = new List<string>(),
                    RefreshedAtUtc = DateTime.UtcNow
                }
            };
            foreach (var response in Responses(xml, "TermsQueryRs", "ShipMethodQueryRs"))
            {
                SetStatus(result, response);
                if (Status(response) != 0) continue;
                foreach (var record in response.Elements())
                {
                    string name = (string)record.Element("Name");
                    if (name == null) continue;
                    if (response.Name == "TermsQueryRs" &&
                        (record.Name == "StandardTermsRet" || record.Name == "DateDrivenTermsRet"))
                        result.Data.CreditTerms.Add(name);
                    else if (response.Name == "ShipMethodQueryRs" && record.Name == "ShipMethodRet")
                        result.Data.ShippingMethods.Add(name);
                }
            }
            if (result.StatusCode != 0) result.Data = null;
            return result;
        }

        public static QBStatusResponse<List<QBCustomerCommercialTermsRecord>> Customers(string xml)
        {
            var result = new QBStatusResponse<List<QBCustomerCommercialTermsRecord>>
                { Data = new List<QBCustomerCommercialTermsRecord>() };
            foreach (var response in Responses(xml, "CustomerQueryRs"))
            {
                SetStatus(result, response);
                if (Status(response) != 0) continue;
                foreach (var customer in response.Elements("CustomerRet"))
                {
                    string account = (string)customer.Element("AccountNumber");
                    if (string.IsNullOrWhiteSpace(account)) continue;
                    var shipAccount = customer.Elements("DataExtRet").FirstOrDefault(field =>
                        string.Equals((string)field.Element("DataExtName"), "Ship Account #", StringComparison.OrdinalIgnoreCase));
                    result.Data.Add(new QBCustomerCommercialTermsRecord
                    {
                        AccountNumber = account.Trim(),
                        CreditTerms = (string)customer.Element("TermsRef")?.Element("FullName"),
                        ShippingTerms = (string)shipAccount?.Element("DataExtValue")
                    });
                }
            }
            if (result.StatusCode != 0) result.Data = null;
            return result;
        }
    }
}
