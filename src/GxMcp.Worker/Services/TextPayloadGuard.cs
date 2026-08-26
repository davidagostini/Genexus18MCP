using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Detects payloads where a client sent the characters "\\r\\n" or "\\n"
    /// instead of real line breaks. Those payloads are especially dangerous when
    /// the first line starts with // because GeneXus treats the entire part as one
    /// comment. The guard is deliberately a rejection, not an automatic rewrite:
    /// the same characters are valid inside a source string literal.
    /// </summary>
    internal static class TextPayloadGuard
    {
        internal const string ErrorCode = "LiteralLineBreaksDetected";

        private sealed class LiteralMatch
        {
            internal string Sequence;
            internal int Index;
        }

        internal sealed class Issue
        {
            internal Issue(bool hasActualLineBreaks, bool startsWithLineComment, List<string> literalSequences, int firstLiteralIndex)
            {
                HasActualLineBreaks = hasActualLineBreaks;
                StartsWithLineComment = startsWithLineComment;
                LiteralSequences = literalSequences;
                FirstLiteralIndex = firstLiteralIndex;
            }

            public bool HasActualLineBreaks { get; }
            public bool StartsWithLineComment { get; }
            public IList<string> LiteralSequences { get; }
            public int FirstLiteralIndex { get; }
        }

        internal static bool AppliesToPart(string partName)
        {
            return string.Equals(partName, "Source", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Rules", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Events", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Code", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Variables", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Conditions", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Parameters", StringComparison.OrdinalIgnoreCase);
        }

        internal static Issue Analyze(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            bool hasActualLineBreaks = text.IndexOf('\r') >= 0 || text.IndexOf('\n') >= 0;
            bool startsWithLineComment = text.TrimStart(' ', '\t', '\uFEFF').StartsWith("//", StringComparison.Ordinal);
            var allMatches = FindLiteralMatches(text, codeOnly: false);
            var codeMatches = FindLiteralMatches(text, codeOnly: true);

            // A source that starts with // and has no physical line break is the
            // exact failure mode where all generated code becomes unreachable.
            bool commentSwallowsPayload = startsWithLineComment
                && !hasActualLineBreaks
                && allMatches.Count > 0;

            if (!commentSwallowsPayload && codeMatches.Count == 0) return null;

            var sequences = new List<string>();
            int firstIndex = int.MaxValue;
            if (commentSwallowsPayload)
                AddMatches(allMatches, sequences, ref firstIndex);
            AddMatches(codeMatches, sequences, ref firstIndex);

            return new Issue(hasActualLineBreaks, startsWithLineComment, sequences, firstIndex == int.MaxValue ? -1 : firstIndex);
        }

        internal static JObject BuildFieldError(string field, string text)
        {
            var issue = Analyze(text);
            if (issue == null) return null;

            return new JObject
            {
                ["field"] = field,
                ["code"] = ErrorCode,
                ["errors"] = new JArray(BuildMessage(field)),
                ["literalSequences"] = ToJArray(issue.LiteralSequences),
                ["hasActualLineBreaks"] = issue.HasActualLineBreaks
            };
        }

        internal static string BuildWriteError(string target, string partName, string field, string text)
        {
            var issue = Analyze(text);
            if (issue == null) return null;

            var extra = new JObject
            {
                ["part"] = partName,
                ["field"] = field,
                ["inputLength"] = text == null ? 0 : text.Length,
                ["literalSequences"] = ToJArray(issue.LiteralSequences),
                ["hasActualLineBreaks"] = issue.HasActualLineBreaks,
                ["firstLiteralIndex"] = issue.FirstLiteralIndex
            };

            return McpResponse.Err(
                code: ErrorCode,
                message: BuildMessage(field),
                hint: "Send the text with real line breaks. Do not send the four-character sequences \\r\\n, \\n, or \\r, and do not double-encode the JSON payload.",
                target: target,
                extra: extra);
        }

        internal static string BuildMessage(string field)
        {
            return "Field '" + (field ?? "text")
                + "' contains literal line-break escape sequences (\\r\\n, \\n, or \\r); send actual line breaks. Nothing was written.";
        }

        private static List<LiteralMatch> FindLiteralMatches(string text, bool codeOnly)
        {
            var matches = new List<LiteralMatch>();
            char quote = '\0';
            bool lineComment = false;
            bool blockComment = false;

            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (codeOnly && lineComment)
                {
                    if (current == '\r' || current == '\n') lineComment = false;
                    continue;
                }

                if (codeOnly && blockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        blockComment = false;
                        i++;
                    }
                    continue;
                }

                if (codeOnly && quote != '\0')
                {
                    if (current == quote)
                    {
                        if (next == quote) i++;
                        else quote = '\0';
                    }
                    continue;
                }

                if (codeOnly && (current == '\'' || current == '"'))
                {
                    quote = current;
                    continue;
                }

                if (codeOnly && current == '/' && next == '/')
                {
                    lineComment = true;
                    i++;
                    continue;
                }

                if (codeOnly && current == '/' && next == '*')
                {
                    blockComment = true;
                    i++;
                    continue;
                }

                string sequence;
                int length = MatchLiteral(text, i, out sequence);
                if (length <= 0) continue;

                matches.Add(new LiteralMatch { Sequence = sequence, Index = i });
                i += length - 1;
            }

            return matches;
        }

        private static int MatchLiteral(string text, int index, out string sequence)
        {
            sequence = null;
            if (index + 1 >= text.Length || text[index] != '\\') return 0;

            if (text[index + 1] == 'r')
            {
                if (index + 3 < text.Length && text[index + 2] == '\\' && text[index + 3] == 'n')
                {
                    sequence = "\\r\\n";
                    return 4;
                }

                sequence = "\\r";
                return 2;
            }

            if (text[index + 1] == 'n')
            {
                sequence = "\\n";
                return 2;
            }

            return 0;
        }

        private static void AddMatches(List<LiteralMatch> matches, List<string> sequences, ref int firstIndex)
        {
            foreach (var match in matches)
            {
                if (match.Index < firstIndex) firstIndex = match.Index;
                if (!sequences.Contains(match.Sequence)) sequences.Add(match.Sequence);
            }
        }

        private static JArray ToJArray(IList<string> values)
        {
            var array = new JArray();
            foreach (var value in values) array.Add(value);
            return array;
        }
    }
}
