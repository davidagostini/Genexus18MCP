using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Pure text matching and editing rules used by mode=patch. This class has
    /// no SDK, cache, persistence, rollback, or response-formatting concerns.
    /// </summary>
    internal static class PatchTextEditor
    {
        internal sealed class NearMatch
        {
            public int StartLine;
            public double Similarity;
            public string Snippet = string.Empty;
        }

        internal static string TryReplace(
            string[] sourceLines,
            string[] contextLines,
            string newContent,
            int expectedCount,
            out string status,
            out string details,
            out int matchCount,
            bool replaceAll = false)
        {
            status = "Applied";
            details = string.Empty;
            matchCount = 0;

            string source = string.Join("\n", sourceLines);
            string context = string.Join("\n", contextLines);

            int exactCount = CountOccurrences(source, context);
            matchCount = exactCount;
            int effectiveExpected = replaceAll && exactCount > 0 ? exactCount : expectedCount;
            if (exactCount == effectiveExpected && exactCount > 0)
            {
                Logger.Info("[PATCH] Exact match found.");
                return source.Replace(context, newContent);
            }
            if (exactCount > 0 && !replaceAll)
            {
                status = "Ambiguous";
                details = $"Ambiguous patch: Found {exactCount} exact matches, but expected {expectedCount}. Provide more context to uniquely identify the block, or pass replaceAll=true to apply to all occurrences.";
                return string.Empty;
            }

            Logger.Info("[PATCH] Exact match failed or count mismatch (" + exactCount + " vs " + expectedCount + "). Attempting fuzzy match.");
            var indices = FindFuzzyMatches(sourceLines, contextLines);
            matchCount = indices.Count;
            int fuzzyEffective = replaceAll && indices.Count > 0 ? indices.Count : expectedCount;

            if (indices.Count == fuzzyEffective && indices.Count > 0)
            {
                var resultLines = new List<string>(sourceLines);
                var replacementLines = NormalizeEol(newContent).Split('\n');
                indices.Sort();
                indices.Reverse();
                foreach (int idx in indices)
                {
                    Logger.Info($"[PATCH] Fuzzy match found at line {idx}.");
                    resultLines.RemoveRange(idx, contextLines.Length);
                    resultLines.InsertRange(idx, replacementLines);
                }
                return string.Join("\n", resultLines);
            }

            if (indices.Count > 0 && !replaceAll)
            {
                status = "Ambiguous";
                details = $"Ambiguous patch: Found {indices.Count} fuzzy matches, but expected {expectedCount}. Provide more context to uniquely identify the block, or pass replaceAll=true to apply to all occurrences.";
                return string.Empty;
            }

            string normalizedSource = NormalizeWhitespace(source);
            string normalizedContext = NormalizeWhitespace(context);
            if (!string.IsNullOrEmpty(normalizedContext))
            {
                int normalizedHits = CountOccurrences(normalizedSource, normalizedContext);
                int normalizedExpected = replaceAll && normalizedHits > 0 ? normalizedHits : expectedCount;
                if (normalizedHits == normalizedExpected && normalizedHits > 0)
                {
                    string rebuilt = TryWhitespaceNormalizedReplace(sourceLines, contextLines, newContent);
                    if (rebuilt != null)
                    {
                        Logger.Info("[PATCH] Whitespace-normalized match applied.");
                        matchCount = normalizedHits;
                        return rebuilt;
                    }
                }
                else if (normalizedHits > 0 && !replaceAll)
                {
                    status = "Ambiguous";
                    matchCount = normalizedHits;
                    details = $"Ambiguous patch (whitespace-normalized): {normalizedHits} matches, expected {expectedCount}. Pass replaceAll=true to apply to every match.";
                    return string.Empty;
                }
            }

            if (expectedCount == 1 && contextLines != null && contextLines.Length > 0)
            {
                if (WriteService.TryMatch(source, context, out int start, out int end) && end > start)
                {
                    Logger.Info("[PATCH] EOL/trailing-whitespace normalized match applied.");
                    matchCount = 1;
                    return source.Substring(0, start) + NormalizeEol(newContent) + source.Substring(end);
                }
            }

            status = "NoMatch";
            details = "Context block not found.";
            return string.Empty;
        }

        internal static string TryInsertAfter(
            string[] sourceLines,
            string[] contextLines,
            string newContent,
            int expectedCount,
            out string status,
            out string details,
            out int matchCount)
        {
            status = "Applied";
            details = string.Empty;
            matchCount = 0;

            var exactIndices = FindExactMatches(sourceLines, contextLines);
            matchCount = exactIndices.Count;
            if (exactIndices.Count == expectedCount && exactIndices.Count > 0)
                return InsertAfterIndices(sourceLines, contextLines, newContent, exactIndices);

            if (exactIndices.Count > 0)
            {
                status = "Ambiguous";
                details = $"Ambiguous anchor: Found {exactIndices.Count} exact matches for the anchor, expected {expectedCount}.";
                return string.Empty;
            }

            var fuzzyIndices = FindFuzzyMatches(sourceLines, contextLines);
            matchCount = fuzzyIndices.Count;
            if (fuzzyIndices.Count == expectedCount && fuzzyIndices.Count > 0)
                return InsertAfterIndices(sourceLines, contextLines, newContent, fuzzyIndices);

            if (fuzzyIndices.Count > 0)
            {
                status = "Ambiguous";
                details = $"Ambiguous anchor: Found {fuzzyIndices.Count} fuzzy matches for the anchor, expected {expectedCount}.";
                return string.Empty;
            }

            status = "NoMatch";
            details = "Anchor block not found.";
            return string.Empty;
        }

        internal static List<NearMatch> FindNearMatches(string[] sourceLines, string[] contextLines, int topN)
        {
            var hits = new List<NearMatch>();
            if (sourceLines == null || contextLines == null) return hits;
            if (contextLines.Length == 0 || sourceLines.Length < contextLines.Length) return hits;

            string[] normalizedSource = new string[sourceLines.Length];
            for (int i = 0; i < sourceLines.Length; i++) normalizedSource[i] = NormalizeWhitespace(sourceLines[i]);
            string[] normalizedContext = new string[contextLines.Length];
            for (int j = 0; j < contextLines.Length; j++) normalizedContext[j] = NormalizeWhitespace(contextLines[j]);

            int maxStart = sourceLines.Length - contextLines.Length;
            for (int i = 0; i <= maxStart; i++)
            {
                int matches = 0;
                for (int j = 0; j < contextLines.Length; j++)
                {
                    if (string.Equals(normalizedSource[i + j], normalizedContext[j], StringComparison.OrdinalIgnoreCase))
                        matches++;
                }
                double similarity = (double)matches / contextLines.Length;
                if (similarity < 0.4) continue;

                string snippet = sourceLines[i].Trim();
                if (snippet.Length > 120) snippet = snippet.Substring(0, 117) + "...";
                hits.Add(new NearMatch { StartLine = i, Similarity = similarity, Snippet = snippet });
            }

            hits.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
            if (hits.Count > topN) hits = hits.GetRange(0, topN);
            return hits;
        }

        internal static string ShowControlChars(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace("\r\n", "↵\n").Replace("\r", "←").Replace("\t", "→");
        }

        internal static int LevenshteinDistance(string a, string b, int maxDist = -1)
        {
            if (a == null) a = string.Empty;
            if (b == null) b = string.Empty;
            int m = a.Length, n = b.Length;
            bool hasLimit = maxDist >= 0;
            if (hasLimit && Math.Abs(m - n) > maxDist) return maxDist + 1;
            if (m == 0) return n;
            if (n == 0) return m;

            const int MaxLen = 4096;
            if (m > MaxLen || n > MaxLen) return hasLimit ? maxDist + 1 : int.MaxValue;

            var previous = new int[n + 1];
            var current = new int[n + 1];
            for (int j = 0; j <= n; j++) previous[j] = j;

            for (int i = 1; i <= m; i++)
            {
                current[0] = i;
                int rowMin = current[0];
                for (int j = 1; j <= n; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                    if (current[j] < rowMin) rowMin = current[j];
                }
                if (hasLimit && rowMin > maxDist) return maxDist + 1;
                var swap = previous;
                previous = current;
                current = swap;
            }
            return previous[n];
        }

        internal static int CountOccurrences(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return 0;
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(pattern, index)) != -1)
            {
                index += pattern.Length;
                count++;
            }
            return count;
        }

        private static string TryWhitespaceNormalizedReplace(string[] sourceLines, string[] contextLines, string newContent)
        {
            if (sourceLines == null || contextLines == null || contextLines.Length == 0) return null;
            if (sourceLines.Length < contextLines.Length) return null;

            string normalizedTarget = NormalizeWhitespace(string.Join("\n", contextLines));
            for (int i = 0; i <= sourceLines.Length - contextLines.Length; i++)
            {
                string window = string.Join("\n", sourceLines, i, contextLines.Length);
                if (NormalizeWhitespace(window) == normalizedTarget)
                {
                    var resultLines = new List<string>(sourceLines);
                    var replacementLines = NormalizeEol(newContent).Split('\n');
                    resultLines.RemoveRange(i, contextLines.Length);
                    resultLines.InsertRange(i, replacementLines);
                    return string.Join("\n", resultLines);
                }
            }
            return null;
        }

        private static List<int> FindFuzzyMatches(string[] sourceLines, string[] targetLines)
        {
            var matches = new List<int>();
            if (targetLines.Length == 0 || sourceLines.Length < targetLines.Length) return matches;

            string normalizedFirst = NormalizeWhitespace(targetLines[0]);
            string normalizedLast = NormalizeWhitespace(targetLines[targetLines.Length - 1]);
            for (int i = 0; i <= sourceLines.Length - targetLines.Length; i++)
            {
                if (!string.Equals(NormalizeWhitespace(sourceLines[i]), normalizedFirst, StringComparison.OrdinalIgnoreCase)) continue;
                int tailIndex = i + targetLines.Length - 1;
                if (!string.Equals(NormalizeWhitespace(sourceLines[tailIndex]), normalizedLast, StringComparison.OrdinalIgnoreCase)) continue;

                bool match = true;
                for (int j = 0; j < targetLines.Length; j++)
                {
                    if (!LinesMatchFuzzy(sourceLines[i + j], targetLines[j]))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) matches.Add(i);
            }
            return matches;
        }

        private static List<int> FindExactMatches(string[] sourceLines, string[] targetLines)
        {
            var matches = new List<int>();
            if (targetLines.Length == 0 || sourceLines.Length < targetLines.Length) return matches;
            for (int i = 0; i <= sourceLines.Length - targetLines.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < targetLines.Length; j++)
                {
                    if (!string.Equals(sourceLines[i + j], targetLines[j], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) matches.Add(i);
            }
            return matches;
        }

        private static string InsertAfterIndices(string[] sourceLines, string[] contextLines, string newContent, List<int> indices)
        {
            var resultLines = new List<string>(sourceLines);
            var insertLines = NormalizeEol(newContent).Split('\n');
            indices.Sort();
            indices.Reverse();
            foreach (int idx in indices) resultLines.InsertRange(idx + contextLines.Length, insertLines);
            return string.Join("\n", resultLines);
        }

        private static bool LinesMatchFuzzy(string left, string right)
        {
            return string.Equals(NormalizeWhitespace(left), NormalizeWhitespace(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return Regex.Replace(value.Trim(), @"\s+", " ");
        }

        private static string NormalizeEol(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        }
    }
}
