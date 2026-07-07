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
