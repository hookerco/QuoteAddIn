using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ExcelAddIn1
{
    internal class ItemLookupCandidate
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

    internal static class ItemLookupCandidateSelector
    {
        internal static string SelectBestItemNumber(
            List<ItemLookupCandidate> candidates,
            string lookupPartNumber,
            string preferredDescriptionPartNumber)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return "";
            }

            List<ItemLookupCandidate> eligibleCandidates = GetEligibleCandidates(candidates, preferredDescriptionPartNumber);
            if (eligibleCandidates.Count == 0)
            {
                return "";
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
            if (preferredWiperKind == "")
            {
                return candidates;
            }

            List<ItemLookupCandidate> eligibleCandidates = new List<ItemLookupCandidate>();
            foreach (ItemLookupCandidate candidate in candidates)
            {
                if (GetWiperKind(FindDescriptionPartNumber(candidate.Description)) == preferredWiperKind)
                {
                    eligibleCandidates.Add(candidate);
                }
            }

            return eligibleCandidates;
        }

        private static string GetWiperKind(string partNumber)
        {
            Match match = Regex.Match(partNumber ?? "", @"^\s*(?<kind>WI|WD)(?=$|[^A-Z])", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return "";
            }

            return match.Groups["kind"].Value.ToUpperInvariant();
        }

        private static int GetDescriptionPartDistance(string description, string preferredDescriptionPartNumber)
        {
            string preferred = NormalizedToken(preferredDescriptionPartNumber);
            if (preferred == "")
            {
                return 0;
            }

            return LevenshteinDistance(NormalizedToken(FindDescriptionPartNumber(description)), preferred);
        }

        private static string FindDescriptionPartNumber(string description)
        {
            Match match = Regex.Match(description ?? "", @"^\s*(?<partNumber>.*?)(?:,|$)");
            if (!match.Success)
            {
                return "";
            }

            return match.Groups["partNumber"].Value;
        }

        private static string NormalizedToken(string value)
        {
            if (value == null)
            {
                return "";
            }

            StringBuilder builder = new StringBuilder();
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
    }
}
