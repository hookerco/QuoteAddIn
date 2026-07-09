using System.Text.RegularExpressions;

namespace QuoteItemResolution
{
    internal static class ItemLookupKey
    {
        internal static string GetLookupPartNumber(string description, string quotePartNumber)
        {
            if (!IsWiperItem(quotePartNumber))
            {
                return quotePartNumber;
            }

            string edpNumber = FindEdpNumber(description);
            if (edpNumber == "")
            {
                return quotePartNumber;
            }

            return edpNumber;
        }

        // The strict EDP-naming rule applies only to wiper INSERT lines: an insert and its
        // paired die share an EDP number, so a die line must never claim the bare EDP as an
        // item name. A line counts as a die when its part number leads with WD or its
        // description uses the product-type phrase "wiper die"; the bare word "die" is not
        // enough, because an insert may merely reference one ("wiper insert for die set").
        internal static string GetInsertEdpNumber(string description, string quotePartNumber)
        {
            if (!IsWiperItem(quotePartNumber))
            {
                return "";
            }

            if (IsWiperDiePartNumber(quotePartNumber) || DescribesWiperDie(description))
            {
                return "";
            }

            return FindEdpNumber(description);
        }

        internal static bool DescribesWiperDie(string description)
        {
            return Regex.IsMatch(description ?? "", @"\bWIPER\s+DIES?\b", RegexOptions.IgnoreCase);
        }

        private static bool IsWiperDiePartNumber(string quotePartNumber)
        {
            return Regex.IsMatch(quotePartNumber ?? "", @"^\s*WD(?=$|[^A-Z])", RegexOptions.IgnoreCase);
        }

        private static bool IsWiperItem(string quotePartNumber)
        {
            return Regex.IsMatch(quotePartNumber ?? "", @"^\s*(?:WI|WD)(?=$|[^A-Z])", RegexOptions.IgnoreCase);
        }

        private static string FindEdpNumber(string description)
        {
            Match match = Regex.Match(
                description ?? "",
                @"\bEDP\s*#\s*:?\s*(?<edp>\d+)",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                return "";
            }

            return match.Groups["edp"].Value;
        }
    }
}
