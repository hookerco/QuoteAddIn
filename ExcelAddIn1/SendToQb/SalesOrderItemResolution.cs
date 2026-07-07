using System.Collections.Generic;
using QuickBooksIPCContracts;
using QuoteItemResolution;

namespace ExcelAddIn1
{
	// Bridges the Send Quote sheet to the shared QuoteItemResolution resolver:
	// maps the sheet items to upload lines, writes the resolved numbers back onto
	// the items, and returns the new items the resolver decided must be created
	// in QuickBooks before the order is sent.
	internal static class SalesOrderItemResolution
	{
		internal static List<QBItem> ResolveNumbers(List<SOSheetQuoteItem> items, IEnumerable<QBItem> catalog)
		{
			var lines = new List<QBQuoteUploadLine>();
			foreach (SOSheetQuoteItem item in items)
			{
				lines.Add(new QBQuoteUploadLine
				{
					Description = item.GetDescription(),
					Quantity = item.GetQuantity(),
					Rate = item.GetRate(),
					OverrideNumber = item.GetOverride()
				});
			}

			QuoteUploadItemResolution resolution = QuoteUploadItemResolver.Resolve(lines, catalog);

			for (int i = 0; i < items.Count; ++i)
			{
				items[i].SetNumber(resolution.ResolvedLines[i].Number);
			}

			return resolution.ItemsToCreate;
		}
	}
}
