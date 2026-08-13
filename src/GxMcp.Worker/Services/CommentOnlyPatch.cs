using System;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Classifies Replace patches that only deactivate an existing Source statement
    /// by surrounding it with a GeneXus line or block comment. It performs no SDK I/O.
    /// </summary>
    internal static class CommentOnlyPatch
    {
        internal static bool TryClassify(
            string partName,
            string operation,
            string context,
            string replacement,
            out string commentStyle)
        {
            commentStyle = null;
            if (!WritePolicy.IsLogicalSourcePart(partName) ||
                !string.Equals(operation, "replace", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(context) || string.IsNullOrEmpty(replacement))
                return false;

            string expected = NormalizeEol(context);
            string candidate = NormalizeEol(replacement);
            if (TryUncommentLines(candidate, out string uncommented) &&
                EqualsWithOnlyTrailingWhitespaceAdded(expected, uncommented))
            {
                commentStyle = "line";
                return true;
            }

            string trimmed = candidate.Trim();
            if (trimmed.StartsWith("/*", StringComparison.Ordinal) &&
                trimmed.EndsWith("*/", StringComparison.Ordinal) &&
                trimmed.Length >= 4)
            {
                string blockBody = trimmed.Substring(2, trimmed.Length - 4).Trim();
                if (string.Equals(blockBody, expected.Trim(), StringComparison.Ordinal))
                {
                    commentStyle = "block";
                    return true;
                }
            }

            return false;
        }

        internal static int CountActiveOccurrences(string source, string statement)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(statement)) return 0;
            bool[] activeStarts = BuildActiveStartMap(source);
            int count = 0;
            int offset = 0;
            while (offset <= source.Length - statement.Length)
            {
                int found = source.IndexOf(statement, offset, StringComparison.Ordinal);
                if (found < 0) break;
                if (activeStarts[found]) count++;
                offset = found + Math.Max(1, statement.Length);
            }
            return count;
        }

        private static bool TryUncommentLines(string value, out string uncommented)
        {
            string[] lines = value.Split(new[] { '\n' }, StringSplitOptions.None);
            var result = new string[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    result[i] = line;
                    continue;
                }
                int marker = 0;
                while (marker < line.Length && (line[marker] == ' ' || line[marker] == '\t')) marker++;
                if (marker + 1 >= line.Length || line[marker] != '/' || line[marker + 1] != '/')
                {
                    uncommented = null;
                    return false;
                }
                result[i] = line.Remove(marker, 2);
            }
            uncommented = string.Join("\n", result);
            return true;
        }

        private static bool[] BuildActiveStartMap(string value)
        {
            var active = new bool[value.Length];
            bool lineComment = false;
            bool blockComment = false;
            bool inString = false;
            char quote = '\0';

            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                char next = i + 1 < value.Length ? value[i + 1] : '\0';
                if (lineComment)
                {
                    if (current == '\r' || current == '\n')
                    {
                        lineComment = false;
                    }
                    continue;
                }
                if (blockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        i++;
                        blockComment = false;
                    }
                    continue;
                }
                if (inString)
                {
                    if (current == quote)
                    {
                        if (next == quote)
                        {
                            i++;
                        }
                        else inString = false;
                    }
                    continue;
                }
                if (current == '\'' || current == '"')
                {
                    active[i] = true;
                    inString = true;
                    quote = current;
                }
                else if (current == '/' && next == '/')
                {
                    i++;
                    lineComment = true;
                }
                else if (current == '/' && next == '*')
                {
                    i++;
                    blockComment = true;
                }
                else active[i] = true;
            }
            return active;
        }

        private static string NormalizeEol(string value) =>
            (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");

        private static bool EqualsWithOnlyTrailingWhitespaceAdded(string expected, string candidate)
        {
            string[] expectedLines = expected.Split(new[] { '\n' }, StringSplitOptions.None);
            string[] candidateLines = candidate.Split(new[] { '\n' }, StringSplitOptions.None);
            if (expectedLines.Length != candidateLines.Length) return false;
            for (int i = 0; i < expectedLines.Length; i++)
            {
                if (!string.Equals(
                    expectedLines[i].TrimEnd(' ', '\t'),
                    candidateLines[i].TrimEnd(' ', '\t'),
                    StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }
}
