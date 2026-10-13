using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Interop.QBFC14;
using QuickBooksIPCContracts;

namespace QBRequestLibrary
{
    // These are existing, explicitly configured native items. Never create them.
    public class QuoteDiscountItemsQueryRequest
        : Request<string[], QBStatusResponse<QBQuoteDiscount>>, IQuoteDiscountItemsQueryRequest
    {
        public QuoteDiscountItemsQueryRequest(string discount, string subtotal, string fingerprint)
        {
            Set(new[] { discount, subtotal });
            RequireCompanyFingerprint(fingerprint);
        }

        protected override void BuildHelper()
        {
            var discount = _msgSetRequest.AppendItemDiscountQueryRq();
            discount.ORListQueryWithOwnerIDAndClass.FullNameList.Add(_value[0]);
            foreach (string field in new[] { "ListID", "FullName", "IsActive" })
                discount.IncludeRetElementList.Add(field);
            var subtotal = _msgSetRequest.AppendItemSubtotalQueryRq();
            subtotal.ORListQuery.FullNameList.Add(_value[1]);
            foreach (string field in new[] { "ListID", "Name", "IsActive" })
                subtotal.IncludeRetElementList.Add(field);
        }

        protected override QBStatusResponse<QBQuoteDiscount> ConvertResponse(IMsgSetResponse response)
        {
            return Parse(response.ToXMLString(), _value[0], _value[1]);
        }

        public static QBStatusResponse<QBQuoteDiscount> Parse(string xml, string discount, string subtotal)
        {
            var failure = new QBStatusResponse<QBQuoteDiscount> {
                StatusCode = 1,
                StatusMessage = "Configure active QuickBooks Discount and Subtotal items for quote discounts."
            };
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                XDocument document;
                using (var reader = XmlReader.Create(new StringReader(xml), settings))
                    document = XDocument.Load(reader);
                var responses = document.Descendants("QBXMLMsgsRs").Single().Elements().ToArray();
                if (responses.Length != 2) return failure;
                var discountResponse = responses.Single(e => e.Name == "ItemDiscountQueryRs");
                var subtotalResponse = responses.Single(e => e.Name == "ItemSubtotalQueryRs");
                if ((string)discountResponse.Attribute("statusCode") != "0" ||
                    (string)subtotalResponse.Attribute("statusCode") != "0") return failure;
                var d = discountResponse.Elements("ItemDiscountRet").Single();
                var s = subtotalResponse.Elements("ItemSubtotalRet").Single();
                if (!string.Equals((string)d.Element("FullName"), discount, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals((string)s.Element("Name"), subtotal, StringComparison.OrdinalIgnoreCase) ||
                    (string)d.Element("IsActive") != "true" || (string)s.Element("IsActive") != "true" ||
                    string.IsNullOrWhiteSpace((string)d.Element("ListID")) ||
                    string.IsNullOrWhiteSpace((string)s.Element("ListID"))) return failure;
                return new QBStatusResponse<QBQuoteDiscount> {
                    StatusCode = 0, StatusMessage = "OK", Data = new QBQuoteDiscount {
                        DiscountItemListId = (string)d.Element("ListID"),
                        SubtotalItemListId = (string)s.Element("ListID")
                    }
                };
            }
            catch (Exception ex) when (ex is XmlException || ex is InvalidOperationException || ex is ArgumentException)
            {
                return failure;
            }
        }
    }

    public static class QuoteDiscountLines
    {
        private static bool Required(QBQuoteDiscount discount)
        {
            if (discount == null) return false;
            if (discount.Amount <= 0 || string.IsNullOrWhiteSpace(discount.DiscountItemListId) ||
                string.IsNullOrWhiteSpace(discount.SubtotalItemListId))
                throw new ArgumentException("A discount requires a positive amount and validated item references.");
            return true;
        }

        public static void Append(IEstimateAdd transaction, QBQuoteDiscount discount)
        {
            if (!Required(discount)) return;
            var subtotal = transaction.OREstimateLineAddList.Append().EstimateLineAdd;
            subtotal.ItemRef.ListID.SetValue(discount.SubtotalItemListId);
            var line = transaction.OREstimateLineAddList.Append().EstimateLineAdd;
            line.ItemRef.ListID.SetValue(discount.DiscountItemListId);
            line.Desc.SetValue(discount.Description ?? "Quote discount");
            // A native Discount item's positive dollar rate is subtracted by QB.
            // No quantity, RatePercent, or Amount: subtotal is calculated by QB.
            line.ORRate.Rate.SetValue((double)discount.Amount);
        }

        public static void Append(ISalesOrderAdd transaction, QBQuoteDiscount discount)
        {
            if (!Required(discount)) return;
            var subtotal = transaction.ORSalesOrderLineAddList.Append().SalesOrderLineAdd;
            subtotal.ItemRef.ListID.SetValue(discount.SubtotalItemListId);
            var line = transaction.ORSalesOrderLineAddList.Append().SalesOrderLineAdd;
            line.ItemRef.ListID.SetValue(discount.DiscountItemListId);
            line.Desc.SetValue(discount.Description ?? "Quote discount");
            line.ORRatePriceLevel.Rate.SetValue((double)discount.Amount);
        }
    }
}
