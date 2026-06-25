using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using QuickBooksIPCContracts;

namespace QuickBooksIPCService
{
    public class QuoteUploadItemResolution
    {
        public List<QBQuoteUploadResolvedLine> ResolvedLines { get; set; } = new List<QBQuoteUploadResolvedLine>();

        public List<QBItem> ItemsToCreate { get; set; } = new List<QBItem>();
    }

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
                    string lookupPartNumber = GetLookupPartNumber(line.Description, quotePartNumber);
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

            return SelectBestItemNumber(candidates, lookup, preferredDescriptionPartNumber);
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

        private static string GetLookupPartNumber(string description, string quotePartNumber)
        {
            if (!IsWiperItem(quotePartNumber))
            {
                return quotePartNumber;
            }

            string edpNumber = FindEdpNumber(description);
            return edpNumber == string.Empty ? quotePartNumber : edpNumber;
        }

        private static bool IsWiperItem(string quotePartNumber)
        {
            return Regex.IsMatch(quotePartNumber ?? string.Empty, @"^\s*(?:WI|WD)(?=$|[^A-Z])", RegexOptions.IgnoreCase);
        }

        private static string FindEdpNumber(string description)
        {
            Match match = Regex.Match(
                description ?? string.Empty,
                @"\bEDP\s*#\s*:?\s*(?<edp>\d+)",
                RegexOptions.IgnoreCase);

            return match.Success ? match.Groups["edp"].Value : string.Empty;
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

        private static string SelectBestItemNumber(
            List<ItemLookupCandidate> candidates,
            string lookupPartNumber,
            string preferredDescriptionPartNumber)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return string.Empty;
            }

            List<ItemLookupCandidate> eligibleCandidates = GetEligibleCandidates(candidates, preferredDescriptionPartNumber);
            if (eligibleCandidates.Count == 0)
            {
                return string.Empty;
            }

            ItemLookupCandidate best = null;
            int bestDistance = int.MaxValue;
            bool bestNumberMatchesLookup = false;

            foreach (ItemLookupCandidate candidate in eligibleCandidates)
            {
                int distance = GetDescriptionPartDistance(candidate.Description, preferredDescriptionPartNumber);
                bool numberMatchesLookup = NormalizedToken(candidate.Number) == NormalizedToken(lookupPartNumber);

                if (best == null ||
                    distance < bestDistance ||
                    (distance == bestDistance && numberMatchesLookup && !bestNumberMatchesLookup) ||
                    (distance == bestDistance &&
                        numberMatchesLookup == bestNumberMatchesLookup &&
                        candidate.OriginalIndex < best.OriginalIndex))
                {
                    best = candidate;
                    bestDistance = distance;
                    bestNumberMatchesLookup = numberMatchesLookup;
                }
            }

            return best.Number;
        }

        private static List<ItemLookupCandidate> GetEligibleCandidates(
            List<ItemLookupCandidate> candidates,
            string preferredDescriptionPartNumber)
        {
            string preferredWiperKind = GetWiperKind(preferredDescriptionPartNumber);
            if (preferredWiperKind == string.Empty)
            {
                return candidates;
            }

            var eligibleCandidates = new List<ItemLookupCandidate>();
            foreach (ItemLookupCandidate candidate in candidates)
            {
                if (GetCandidateWiperKind(candidate.Description) == preferredWiperKind)
                {
                    eligibleCandidates.Add(candidate);
                }
            }

            return eligibleCandidates;
        }

        private static string GetWiperKind(string partNumber)
        {
            Match match = Regex.Match(partNumber ?? string.Empty, @"^\s*(?<kind>WI|WD)(?=$|[^A-Z])", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["kind"].Value.ToUpperInvariant() : string.Empty;
        }

        // Classifies a catalog candidate as a wiper insert ("WI") or wiper die ("WD").
        // A die is routinely entered in QuickBooks with the paired insert's "WI-" part number as its
        // leading token, so the leading token alone is unreliable. The product-type phrase "wiper die"
        // marks a die regardless of the prefix it leads with; the bare word "die" is not enough,
        // because an insert description may merely reference one (e.g. "wiper insert for die set").
        private static string GetCandidateWiperKind(string description)
        {
            string leadKind = GetWiperKind(FindDescriptionPartNumber(description));
            if (leadKind == string.Empty)
            {
                return string.Empty;
            }

            if (DescribesWiperDie(description))
            {
                return "WD";
            }

            return leadKind;
        }

        private static bool DescribesWiperDie(string description)
        {
            return Regex.IsMatch(description ?? string.Empty, @"\bWIPER\s+DIES?\b", RegexOptions.IgnoreCase);
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

        private static int GetDescriptionPartDistance(string description, string preferredDescriptionPartNumber)
        {
            string preferred = NormalizedToken(preferredDescriptionPartNumber);
            if (preferred == string.Empty)
            {
                return 0;
            }

            return LevenshteinDistance(NormalizedToken(FindDescriptionPartNumber(description)), preferred);
        }

        private static string FindDescriptionPartNumber(string description)
        {
            Match match = Regex.Match(description ?? string.Empty, @"^\s*(?<partNumber>.*?)(?:,|$)");
            return match.Success ? match.Groups["partNumber"].Value : string.Empty;
        }

        private static string NormalizedToken(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (char c in value.ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        private static int LevenshteinDistance(string left, string right)
        {
            int[,] distances = new int[left.Length + 1, right.Length + 1];

            for (int i = 0; i <= left.Length; i++)
            {
                distances[i, 0] = i;
            }

            for (int j = 0; j <= right.Length; j++)
            {
                distances[0, j] = j;
            }

            for (int i = 1; i <= left.Length; i++)
            {
                for (int j = 1; j <= right.Length; j++)
                {
                    int substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                    int deletion = distances[i - 1, j] + 1;
                    int insertion = distances[i, j - 1] + 1;
                    int substitution = distances[i - 1, j - 1] + substitutionCost;

                    distances[i, j] = System.Math.Min(System.Math.Min(deletion, insertion), substitution);
                }
            }

            return distances[left.Length, right.Length];
        }

        private class ItemLookupCandidate
        {
            internal ItemLookupCandidate(string number, string description, int originalIndex)
            {
                Number = number;
                Description = description;
                OriginalIndex = originalIndex;
            }

            internal string Number { get; }
            internal string Description { get; }
            internal int OriginalIndex { get; }
        }
    }
}
