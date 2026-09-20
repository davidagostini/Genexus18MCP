using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Gateway
{
    public static class DidYouMean
    {
        public static int Levenshtein(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            if (b.Length <= 128)
            {
                Span<int> prev = stackalloc int[b.Length + 1];
                Span<int> curr = stackalloc int[b.Length + 1];
                for (int j = 0; j <= b.Length; j++) prev[j] = j;
                for (int i = 1; i <= a.Length; i++)
                {
                    curr[0] = i;
                    char ca = char.ToLowerInvariant(a[i - 1]);
                    for (int j = 1; j <= b.Length; j++)
                    {
                        int cost = ca == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                        curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                    }
                    curr.CopyTo(prev);
                }
                return prev[b.Length];
            }
            else
            {
                var prev = new int[b.Length + 1];
                var curr = new int[b.Length + 1];
                for (int j = 0; j <= b.Length; j++) prev[j] = j;
                for (int i = 1; i <= a.Length; i++)
                {
                    curr[0] = i;
                    char ca = char.ToLowerInvariant(a[i - 1]);
                    for (int j = 1; j <= b.Length; j++)
                    {
                        int cost = ca == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                        curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                    }
                    Array.Copy(curr, prev, curr.Length);
                }
                return prev[b.Length];
            }
        }

        public static string? Suggest(string input, IEnumerable<string> candidates, int maxDistance = 2)
        {
            if (string.IsNullOrEmpty(input)) return null;
            string? best = null;
            int bestDist = int.MaxValue;
            foreach (var candidate in candidates)
            {
                if (candidate == null) continue;
                if (Math.Abs(input.Length - candidate.Length) > maxDistance) continue;
                int d = Levenshtein(input, candidate);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = candidate;
                    if (d == 0) break;
                }
            }
            return bestDist <= maxDistance ? best : null;
        }

        public static string FormatSuggestionMessage(string field, string value, IEnumerable<string> candidates, int maxDistance = 2)
        {
            var list = candidates.ToList();
            var suggestion = Suggest(value, list, maxDistance);
            string allowed = string.Join(", ", list);
            if (suggestion != null)
            {
                return $"Invalid {field} '{value}'. Did you mean '{suggestion}'? Allowed: {allowed}.";
            }
            return $"Invalid {field} '{value}'. Allowed: {allowed}.";
        }
    }
}
