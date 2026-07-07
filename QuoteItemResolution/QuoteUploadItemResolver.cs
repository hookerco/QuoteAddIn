using System.Collections.Generic;
using System.Text.RegularExpressions;
using QuickBooksIPCContracts;

namespace QuoteItemResolution
{
    public class QuoteUploadItemResolution
    {
        public List<QBQuoteUploadResolvedLine> ResolvedLines { get; set; } = new List<QBQuoteUploadResolvedLine>();

        public List<QBItem> ItemsToCreate { get; set; } = new List<QBItem>();
    }

    // Single source of truth for turning quote lines into QuickBooks item numbers.
    // Compiled into both the IPC service and the Excel add-in via linked source, so
    // both upload paths allocate identical numbers. This is a financial write path:
    // any behavior change here changes which items get created in QuickBooks.
    //
    // Linked by: QuickBooksIPCService\QuickBooksIPCServiceLibrary.csproj,
    // ExcelAddIn1\QuoteUtility.csproj, and
    // QuickbooksIPCUnitTests\QuickBooksServiceLibrary.Tests.csproj (this folder has no
    // csproj of its own - update all three if these files move or are renamed).
    public static class QuoteUploadItemResolver
    {
        public static QuoteUploadItemResolution Resolve(
            IEnumerable<QBQuoteUploadLine> lines,
            IEnumerable<QBItem> catalogItems)
        {
            var catalog = new List<QBItem>(catalogItems ?? new List<QBItem>());
            var reservedNumbers = GetReservedOneDashNumbers(catalog);
            var activeCatalog = GetActiveCatalog(catalog);
            var numbersToCreate = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var result = new QuoteUploadItemResolution();

            foreach (QBQuoteUploadLine line in lines ?? new List<QBQuoteUploadLine>())
            {
                string number;
                bool createdItem = false;
                string overrideNumber = (line.OverrideNumber ?? string.Empty).Trim();

                if (overrideNumber != string.Empty)
                {
                    number = overrideNumber;
                    if (!ContainsActiveNumber(activeCatalog, overrideNumber) && numbersToCreate.Add(overrideNumber))
                    {
                        ReserveOneDashNumber(reservedNumbers, overrideNumber);
                        QBItem item = CreateNonInventoryItem(overrideNumber, line.Description);
                        result.ItemsToCreate.Add(item);
                        activeCatalog.Add(item);
                        createdItem = true;
                    }
                    else
                    {
                        createdItem = !ContainsActiveNumber(activeCatalog, overrideNumber);
                    }
                }
                else
                {
                    string quotePartNumber = FindPartNumber(line.Description);
                    string lookupPartNumber = ItemLookupKey.GetLookupPartNumber(line.Description, quotePartNumber);
                    number = FindMatchingItemNumber(activeCatalog, lookupPartNumber, quotePartNumber);

                    if (number == string.Empty)
                    {
                        number = GetDieSetItemNumber(quotePartNumber);
                    }

                    if (number == string.Empty)
                    {
                        number = GenerateNumber(reservedNumbers);
                        numbersToCreate.Add(number);
                        QBItem item = CreateNonInventoryItem(number, line.Description);
                        result.ItemsToCreate.Add(item);
                        activeCatalog.Add(item);
                        createdItem = true;
                    }
                }

                result.ResolvedLines.Add(new QBQuoteUploadResolvedLine
                {
                    Number = number,
                    Description = line.Description,
                    Quantity = line.Quantity,
                    Rate = line.Rate,
                    CreatedItem = createdItem
                });
            }

            return result;
        }

        private static List<QBItem> GetActiveCatalog(List<QBItem> catalog)
        {
            var activeCatalog = new List<QBItem>();
            foreach (QBItem item in catalog)
            {
                if (item != null && item.Active)
                {
                    activeCatalog.Add(item);
                }
            }

            return activeCatalog;
        }

        private static HashSet<int> GetReservedOneDashNumbers(List<QBItem> catalog)
        {
            var reservedNumbers = new HashSet<int>();
            foreach (QBItem item in catalog)
            {
                Match match = Regex.Match(item?.Number ?? string.Empty, @"^1-(?<number>\d+).*?$");
                if (match.Success)
                {
                    reservedNumbers.Add(int.Parse(match.Groups["number"].Value));
                }
            }

            return reservedNumbers;
        }

        private static void ReserveOneDashNumber(HashSet<int> reservedNumbers, string number)
        {
            Match match = Regex.Match(number ?? string.Empty, @"^1-(?<number>\d+).*?$");
            if (match.Success)
            {
                reservedNumbers.Add(int.Parse(match.Groups["number"].Value));
            }
        }

        private static bool ContainsActiveNumber(List<QBItem> activeCatalog, string number)
        {
            foreach (QBItem item in activeCatalog)
            {
                if (System.StringComparer.OrdinalIgnoreCase.Equals(item.Number ?? string.Empty, number ?? string.Empty))
                {
                    return true;
                }
            }

            return false;
        }

        private static QBItem CreateNonInventoryItem(string number, string description)
        {
            return new QBItem
            {
                Number = number,
                Description = description,
                AccountName = "Sales Income",
                Active = true
            };
        }

        private static string FindMatchingItemNumber(
            List<QBItem> activeCatalog,
            string lookupPartNumber,
            string preferredDescriptionPartNumber)
        {
            string lookup = (lookupPartNumber ?? string.Empty).ToUpperInvariant();
            var candidates = new List<ItemLookupCandidate>();

            for (int i = 0; i < activeCatalog.Count; ++i)
            {
                QBItem item = activeCatalog[i];
                string description = (item.Description ?? string.Empty).ToUpperInvariant();
                string number = (item.Number ?? string.Empty).ToUpperInvariant();

                if (DescriptionContainsLookup(description, lookup) && IsOurPartNumber(number))
                {
                    candidates.Add(new ItemLookupCandidate(number, description, i));
                }
            }

            return ItemLookupCandidateSelector.SelectBestItemNumber(candidates, lookup, preferredDescriptionPartNumber);
        }

        private static string FindPartNumber(string description)
        {
            Match commaMatch = Regex.Match(description ?? string.Empty, @"^\s*(?<partNumber>.*?)(?:,|$)");
            if (commaMatch.Success)
            {
                return commaMatch.Groups["partNumber"].Value.Trim();
            }

            return string.Empty;
        }

        private static bool IsOurPartNumber(string partNumber)
        {
            if (Regex.IsMatch(partNumber ?? string.Empty, @"^(?:\d-)?\d+$"))
            {
                return true;
            }

            return (partNumber ?? string.Empty).ToUpperInvariant().Contains("SPEC");
        }

        private static string GetDieSetItemNumber(string partNumber)
        {
            string value = partNumber ?? string.Empty;
            if (value.StartsWith("BB/", System.StringComparison.OrdinalIgnoreCase))
            {
                return "1-4501";
            }

            if (value.StartsWith("CI/", System.StringComparison.OrdinalIgnoreCase))
            {
                return "1-4502";
            }

            if (value.StartsWith("CD", System.StringComparison.OrdinalIgnoreCase))
            {
                return "1-4503";
            }

            if (value.StartsWith("PD", System.StringComparison.OrdinalIgnoreCase))
            {
                return "1-4504";
            }

            return string.Empty;
        }

        private static string GenerateNumber(HashSet<int> reservedNumbers)
        {
            int number = 0;
            while (reservedNumbers.Contains(number))
            {
                number++;
            }

            reservedNumbers.Add(number);
            return "1-" + number.ToString("D4");
        }

        // Wiper lookups collapse the key to a bare EDP number, which is then matched against item
        // descriptions. Match a numeric key as a whole number so EDP "3819" does not match inside a
        // longer run of digits such as "38190" or another item's "EDP#13819".
        private static bool DescriptionContainsLookup(string description, string lookup)
        {
            if (string.IsNullOrEmpty(lookup))
            {
                return false;
            }

            if (Regex.IsMatch(lookup, @"^\d+$"))
            {
                return Regex.IsMatch(description ?? string.Empty, @"(?<!\d)" + Regex.Escape(lookup) + @"(?!\d)");
            }

            return (description ?? string.Empty).Contains(lookup);
        }
    }
}
