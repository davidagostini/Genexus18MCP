using System;
using System.Collections.Generic;
using System.Text;
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

        // Issue #205: a resolved `patch.scope` — the editable region is the line range
        // BETWEEN the anchors. The anchors themselves always stay outside it.
        internal sealed class ScopeSlice
        {
            public int StartLine;            // 0-based, first editable line
            public int EndLineExclusive;     // 0-based, exclusive
            public bool EndsAtEof;
        }

        // Where a match landed in the ORIGINAL source lines. StartColumn is what the
        // indentation contract (#206) needs: it is empty for a line-start match and equals
        // the base indent for a match that starts right after the existing indentation.
        internal sealed class MatchSpan
        {
            public int StartLine;            // 0-based
            public int EndLineExclusive;     // 0-based, exclusive
            public int StartColumn;          // 0-based column of the match start within StartLine
            public string Strategy = string.Empty;
        }

        internal sealed class ScopedReplaceOutcome
        {
            public string Status = "Applied";
            public string Details = string.Empty;
            public int MatchCount;
            public string UpdatedSource;
            public readonly List<MatchSpan> Matches = new List<MatchSpan>();
            // Editable region, 0-based (issue #205 rule 6); the caller converts to 1-based
            // exclusive line numbers for the response evidence.
            public int EditableStartLine;
            public int EditableEndLineExclusive;
            public bool EndsAtEof = true;
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

        // Issue #205: split a scope anchor into its complete lines. Only CRLF/LF are
        // normalized — spaces, tabs and every other character must match exactly.
        internal static string[] SplitAnchorLines(string anchor)
        {
            if (anchor == null) return null;
            string normalized = anchor.Replace("\r\n", "\n").Replace("\r", "\n");
            // A trailing line break is the last line's TERMINATOR (a caller pasting the line
            // out of a read output naturally includes it), not an extra empty anchor line.
            if (normalized.EndsWith("\n", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }
            return normalized.Split('\n');
        }

        /// <summary>
        /// Issue #205: resolve a line-anchored scope. `start` must occur exactly once as a
        /// complete-line window; `end` (when present) must occur exactly once in the suffix
        /// after start's last line. Returns null with a code when the anchors are unusable —
        /// an anchor that does not occupy whole lines is <c>ScopeAnchorNotComparable</c>, not
        /// an ordinary miss.
        /// </summary>
        internal static ScopeSlice ResolveScope(
            string[] sourceLines,
            string[] startLines,
            string[] endLines,
            out string errorCode,
            out string errorMessage)
        {
            errorCode = null;
            errorMessage = null;
            if (sourceLines == null || startLines == null || startLines.Length == 0)
            {
                errorCode = "ScopeStartRequired";
                errorMessage = "patch.scope.start is required.";
                return null;
            }

            string sourceText = string.Join("\n", sourceLines);
            int startIndex = ResolveAnchor(sourceText, sourceLines, startLines, 0, out errorCode, out errorMessage, "scope.start");
            if (startIndex < 0) return null;

            int scopeStart = startIndex + startLines.Length;
            if (endLines == null || endLines.Length == 0)
            {
                return new ScopeSlice { StartLine = scopeStart, EndLineExclusive = sourceLines.Length, EndsAtEof = true };
            }

            string suffixText = string.Join("\n", sourceLines, scopeStart, sourceLines.Length - scopeStart);
            int suffixIndex = ResolveAnchor(suffixText, sourceLines, endLines, scopeStart, out errorCode, out errorMessage, "scope.end");
            if (suffixIndex < 0) return null;

            return new ScopeSlice { StartLine = scopeStart, EndLineExclusive = suffixIndex, EndsAtEof = false };
        }

        // Locate one anchor as a whole-line window at or after `minLine`. Returns the absolute
        // 0-based line index, or -1 with a code set.
        private static int ResolveAnchor(
            string searchText,
            string[] fullSourceLines,
            string[] anchorLines,
            int minLine,
            out string errorCode,
            out string errorMessage,
            string anchorName)
        {
            errorCode = null;
            errorMessage = null;
            var window = FindLineWindowIndices(fullSourceLines, anchorLines, minLine);
            if (window.Count == 1) return window[0];
            if (window.Count > 1)
            {
                errorCode = "ScopeAnchorAmbiguous";
                errorMessage = $"{anchorName} matches {window.Count} complete-line windows; it must be unique. Extend the anchor with more lines.";
                return -1;
            }

            // No whole-line window: distinguish "the anchor is a mid-line fragment" (not
            // comparable by design) from "the anchor is simply absent".
            string anchorText = string.Join("\n", anchorLines);
            if (!string.IsNullOrEmpty(anchorText) && CountOccurrences(searchText, anchorText) > 0)
            {
                errorCode = "ScopeAnchorNotComparable";
                errorMessage = $"{anchorName} does not occupy complete lines of the part; anchors must start at a line start and end at a line end. Comparison normalizes only CRLF/LF.";
                return -1;
            }

            errorCode = "ScopeAnchorNotFound";
            errorMessage = $"{anchorName} was not found in the part.";
            return -1;
        }

        private static List<int> FindLineWindowIndices(string[] sourceLines, string[] anchorLines, int minLine)
        {
            var hits = new List<int>();
            if (anchorLines.Length == 0 || sourceLines.Length < anchorLines.Length) return hits;
            int maxStart = sourceLines.Length - anchorLines.Length;
            for (int i = Math.Max(0, minLine); i <= maxStart; i++)
            {
                bool match = true;
                for (int j = 0; j < anchorLines.Length; j++)
                {
                    if (!string.Equals(sourceLines[i + j], anchorLines[j], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) hits.Add(i);
            }
            return hits;
        }

        /// <summary>
        /// Issue #205/#206: the matching pipeline restricted to [scopeStartLine, scopeEndLineExclusive).
        /// Runs the SAME strategy chain as <see cref="TryReplace"/> (exact → fuzzy →
        /// whitespace-normalized → EOL/trailing-whitespace normalized) over the slice, so no
        /// strategy can escape the scope, and reports where each match landed in the ORIGINAL
        /// line numbering. Only used when `scope` or `indentation` is requested; the unscoped
        /// path is untouched.
        /// </summary>
        internal static ScopedReplaceOutcome ReplaceWithinScope(
            string[] sourceLines,
            int scopeStartLine,
            int scopeEndLineExclusive,
            string[] contextLines,
            string newContent,
            int expectedCount,
            bool replaceAll)
        {
            var outcome = new ScopedReplaceOutcome();
            int start = Math.Max(0, Math.Min(scopeStartLine, sourceLines.Length));
            int end = Math.Max(start, Math.Min(scopeEndLineExclusive, sourceLines.Length));
            var sliceLines = new string[end - start];
            Array.Copy(sourceLines, start, sliceLines, 0, sliceLines.Length);

            string sliceText = string.Join("\n", sliceLines);
            string context = string.Join("\n", contextLines ?? new string[0]);

            // 1. exact substring match (mirrors TryReplace's exact branch)
            int exactCount = CountOccurrences(sliceText, context);
            outcome.MatchCount = exactCount;
            int effectiveExpected = replaceAll && exactCount > 0 ? exactCount : expectedCount;
            if (exactCount == effectiveExpected && exactCount > 0)
            {
                foreach (int offset in FindTextOffsets(sliceText, context))
                {
                    outcome.Matches.Add(MapTextSpan(sliceText, offset, offset + context.Length, start, "exact"));
                }
                outcome.UpdatedSource = Splice(sourceLines, start, end, sliceText.Replace(context, newContent));
                return outcome;
            }
            if (exactCount > 0 && !replaceAll)
            {
                outcome.Status = "Ambiguous";
                outcome.Details = $"Ambiguous patch: Found {exactCount} exact matches inside the scope, but expected {expectedCount}. Provide more context to uniquely identify the block, or pass replaceAll=true to apply to all occurrences.";
                return outcome;
            }

            // 2. fuzzy line-window match
            var fuzzyIndices = FindFuzzyMatches(sliceLines, contextLines);
            outcome.MatchCount = fuzzyIndices.Count;
            int fuzzyEffective = replaceAll && fuzzyIndices.Count > 0 ? fuzzyIndices.Count : expectedCount;
            if (fuzzyIndices.Count == fuzzyEffective && fuzzyIndices.Count > 0)
            {
                var resultLines = new List<string>(sliceLines);
                var replacementLines = NormalizeEol(newContent).Split('\n');
                foreach (int idx in fuzzyIndices)
                {
                    outcome.Matches.Add(LineSpan(idx, contextLines.Length, start, "fuzzy"));
                }
                var ordered = new List<int>(fuzzyIndices);
                ordered.Sort();
                ordered.Reverse();
                foreach (int idx in ordered)
                {
                    resultLines.RemoveRange(idx, contextLines.Length);
                    resultLines.InsertRange(idx, replacementLines);
                }
                outcome.Matches.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
                outcome.UpdatedSource = Splice(sourceLines, start, end, string.Join("\n", resultLines));
                return outcome;
            }
            if (fuzzyIndices.Count > 0 && !replaceAll)
            {
                outcome.Status = "Ambiguous";
                outcome.Details = $"Ambiguous patch: Found {fuzzyIndices.Count} fuzzy matches inside the scope, but expected {expectedCount}. Provide more context to uniquely identify the block, or pass replaceAll=true to apply to all occurrences.";
                return outcome;
            }

            // 3. whitespace-normalized match
            string normalizedSlice = NormalizeWhitespace(sliceText);
            string normalizedContext = NormalizeWhitespace(context);
            if (!string.IsNullOrEmpty(normalizedContext))
            {
                int normalizedHits = CountOccurrences(normalizedSlice, normalizedContext);
                int normalizedExpected = replaceAll && normalizedHits > 0 ? normalizedHits : expectedCount;
                if (normalizedHits == normalizedExpected && normalizedHits > 0)
                {
                    string rebuilt = TryWhitespaceNormalizedReplace(sliceLines, contextLines, newContent);
                    if (rebuilt != null)
                    {
                        int window = FindWhitespaceNormalizedWindow(sliceLines, contextLines);
                        if (window >= 0) outcome.Matches.Add(LineSpan(window, contextLines.Length, start, "whitespace-normalized"));
                        outcome.MatchCount = normalizedHits;
                        outcome.UpdatedSource = Splice(sourceLines, start, end, rebuilt);
                        return outcome;
                    }
                }
                else if (normalizedHits > 0 && !replaceAll)
                {
                    outcome.Status = "Ambiguous";
                    outcome.MatchCount = normalizedHits;
                    outcome.Details = $"Ambiguous patch (whitespace-normalized): {normalizedHits} matches inside the scope, expected {expectedCount}. Pass replaceAll=true to apply to every match.";
                    return outcome;
                }
            }

            // 4. EOL/trailing-whitespace normalized single match
            if (expectedCount == 1 && contextLines != null && contextLines.Length > 0
                && WriteService.TryMatch(sliceText, context, out int matchStart, out int matchEnd) && matchEnd > matchStart)
            {
                outcome.MatchCount = 1;
                outcome.Matches.Add(MapTextSpan(sliceText, matchStart, matchEnd, start, "eol-normalized"));
                outcome.UpdatedSource = Splice(sourceLines, start, end, sliceText.Substring(0, matchStart) + NormalizeEol(newContent) + sliceText.Substring(matchEnd));
                return outcome;
            }

            outcome.Status = "NoMatch";
            outcome.Details = "Context block not found.";
            return outcome;
        }

        private static MatchSpan LineSpan(int sliceLine, int lineCount, int sliceStartLine, string strategy)
        {
            return new MatchSpan
            {
                StartLine = sliceStartLine + sliceLine,
                EndLineExclusive = sliceStartLine + sliceLine + lineCount,
                StartColumn = 0,
                Strategy = strategy
            };
        }

        private static MatchSpan MapTextSpan(string sliceText, int startOffset, int endOffset, int sliceStartLine, string strategy)
        {
            int startLine = LineIndexOf(sliceText, startOffset);
            int endLine = LineIndexOf(sliceText, Math.Max(startOffset, endOffset - 1));
            return new MatchSpan
            {
                StartLine = sliceStartLine + startLine,
                EndLineExclusive = sliceStartLine + endLine + 1,
                StartColumn = startOffset - LineStartOffset(sliceText, startLine),
                Strategy = strategy
            };
        }

        private static int LineIndexOf(string text, int offset)
        {
            int line = 0;
            int max = Math.Min(offset, text.Length);
            for (int i = 0; i < max; i++) if (text[i] == '\n') line++;
            return line;
        }

        private static int LineStartOffset(string text, int lineIndex)
        {
            int line = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (line == lineIndex) return i;
                if (text[i] == '\n') line++;
            }
            return text.Length;
        }

        private static List<int> FindTextOffsets(string text, string pattern)
        {
            var offsets = new List<int>();
            if (string.IsNullOrEmpty(pattern)) return offsets;
            int index = 0;
            while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
            {
                offsets.Add(index);
                index += pattern.Length;
            }
            return offsets;
        }

        private static string Splice(string[] sourceLines, int start, int end, string updatedSlice)
        {
            string prefix = start > 0 ? string.Join("\n", sourceLines, 0, start) + "\n" : string.Empty;
            string suffix = end < sourceLines.Length ? "\n" + string.Join("\n", sourceLines, end, sourceLines.Length - end) : string.Empty;
            return prefix + updatedSlice + suffix;
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

            int i = FindWhitespaceNormalizedWindow(sourceLines, contextLines);
            if (i < 0) return null;

            var resultLines = new List<string>(sourceLines);
            var replacementLines = NormalizeEol(newContent).Split('\n');
            resultLines.RemoveRange(i, contextLines.Length);
            resultLines.InsertRange(i, replacementLines);
            return string.Join("\n", resultLines);
        }

        // Extracted so the scoped path can report the matched window (issue #205/#206) without
        // duplicating the comparison.
        private static int FindWhitespaceNormalizedWindow(string[] sourceLines, string[] contextLines)
        {
            if (sourceLines == null || contextLines == null || contextLines.Length == 0) return -1;
            if (sourceLines.Length < contextLines.Length) return -1;

            string normalizedTarget = NormalizeWhitespace(string.Join("\n", contextLines));
            for (int i = 0; i <= sourceLines.Length - contextLines.Length; i++)
            {
                string window = string.Join("\n", sourceLines, i, contextLines.Length);
                if (NormalizeWhitespace(window) == normalizedTarget) return i;
            }
            return -1;
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
            string trimmed = value.Trim();
            if (trimmed.Length == 0) return string.Empty;

            // Fast path: if there are no consecutive spaces or non-space whitespace chars, return trimmed
            bool hasMultipleWs = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (char.IsWhiteSpace(trimmed[i]) && (trimmed[i] != ' ' || (i + 1 < trimmed.Length && char.IsWhiteSpace(trimmed[i + 1]))))
                {
                    hasMultipleWs = true;
                    break;
                }
            }
            if (!hasMultipleWs) return trimmed;

            var sb = new StringBuilder(trimmed.Length);
            bool inWs = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (char.IsWhiteSpace(c))
                {
                    if (!inWs)
                    {
                        sb.Append(' ');
                        inWs = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    inWs = false;
                }
            }
            return sb.ToString();
        }

        private static string NormalizeEol(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        }
    }
}
