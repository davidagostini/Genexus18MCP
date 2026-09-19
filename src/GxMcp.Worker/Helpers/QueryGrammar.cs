using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    public sealed class ParsedQueryCriteria
    {
        public string RawQuery { get; set; }
        public List<string> FreeTerms { get; } = new List<string>();
        public string TypeFilter { get; set; }
        public string DomainFilter { get; set; }
        public string ParentFilter { get; set; }
        public string ParentPathFilter { get; set; }
        public string UsedByFilter { get; set; }
        public string MetadataFilter { get; set; }
        public string NameFilter { get; set; }
        public string DescriptionFilter { get; set; }

        public bool HasStructuredFilters =>
            !string.IsNullOrWhiteSpace(TypeFilter) ||
            !string.IsNullOrWhiteSpace(DomainFilter) ||
            !string.IsNullOrWhiteSpace(ParentFilter) ||
            !string.IsNullOrWhiteSpace(ParentPathFilter) ||
            !string.IsNullOrWhiteSpace(UsedByFilter) ||
            !string.IsNullOrWhiteSpace(MetadataFilter) ||
            !string.IsNullOrWhiteSpace(NameFilter) ||
            !string.IsNullOrWhiteSpace(DescriptionFilter);
    }

    /// <summary>
    /// Deep Query Grammar Module for GeneXus MCP Search & Query Subsystems.
    /// Unifies prefix extraction (type:, parent:, usedby:, metadata:), keyword tokenization,
    /// and canonical type alias normalization across SearchService, ListService, and SourceSearchService.
    /// </summary>
    public static class QueryGrammar
    {
        private static readonly Dictionary<string, string> TypeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prc"] = "Procedure",
            ["procedure"] = "Procedure",
            ["trn"] = "Transaction",
            ["transaction"] = "Transaction",
            ["wp"] = "WebPanel",
            ["webpanel"] = "WebPanel",
            ["panel"] = "Panel",
            ["sdt"] = "SDT",
            ["structureddatatype"] = "SDT",
            ["dso"] = "DesignSystem",
            ["designsystem"] = "DesignSystem",
            ["dom"] = "Domain",
            ["domain"] = "Domain",
            ["att"] = "Attribute",
            ["attribute"] = "Attribute",
            ["tbl"] = "Table",
            ["table"] = "Table",
            ["sd"] = "SDPanel",
            ["sdpanel"] = "SDPanel",
            ["dv"] = "DataView",
            ["dataview"] = "DataView",
            ["ds"] = "DataSelector",
            ["dataselector"] = "DataSelector",
            ["dp"] = "DataProvider",
            ["dataprovider"] = "DataProvider",
            ["api"] = "API",
            ["module"] = "Module",
            ["mod"] = "Module",
            ["folder"] = "Folder",
            ["fld"] = "Folder",
            ["ws"] = "WebService",
            ["ep"] = "Enterprise"
        };

        private struct FilterPattern
        {
            public string Key;
            public Regex Regex;
            public FilterPattern(string key, Regex regex) { Key = key; Regex = regex; }
        }

        private static readonly FilterPattern[] FilterPatterns = new[]
        {
            new FilterPattern("description", new Regex(@"(?:^|\s)description:(?:""(?<quoted>[^""]+)""|(?<plain>\S+))", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            new FilterPattern("metadata", new Regex(@"(?:^|\s)metadata:(?:""(?<quoted>[^""]+)""|(?<plain>\S+))", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            new FilterPattern("usedby", new Regex(@"(?:^|\s)usedby:(?:""(?<quoted>[^""]+)""|(?<plain>\S+))", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            new FilterPattern("parentPath", new Regex(@"(?:^|\s)parentPath:(?:""(?<quoted>[^""]+)""|(?<plain>\S+))", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            new FilterPattern("parent", new Regex(@"(?:^|\s)parent:(?:""(?<quoted>[^""]+)""|(?<plain>\S+))", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            new FilterPattern("type", new Regex(@"(?:^|\s)type:(?:""(?<quoted>[^""]+)""|(?<plain>\S+))", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            new FilterPattern("name", new Regex(@"(?:^|\s)name:(?:""(?<quoted>[^""]+)""|(?<plain>\S+))", RegexOptions.IgnoreCase | RegexOptions.Compiled))
        };

        private static readonly Regex QuotedOrPlainToken = new Regex(
            @"""(?<quoted>[^""]+)""|(?<plain>\S+)",
            RegexOptions.Compiled);

        private static readonly char[] WhitespaceSeparators = { ' ', '\t', '\r', '\n' };

        public static string NormalizeType(string rawType)
        {
            if (string.IsNullOrWhiteSpace(rawType)) return null;
            string trimmed = rawType.Trim();
            if (TypeAliases.TryGetValue(trimmed, out var canonical)) return canonical;
            return trimmed;
        }

        public static bool IsTypeMatch(string actualType, string expectedType)
        {
            if (string.IsNullOrWhiteSpace(expectedType)) return true;
            if (string.IsNullOrWhiteSpace(actualType)) return false;

            string normExpected = NormalizeType(expectedType);
            string normActual = NormalizeType(actualType);

            if (string.Equals(normActual, normExpected, StringComparison.OrdinalIgnoreCase)) return true;
            if (actualType.IndexOf(expectedType, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (normActual.IndexOf(normExpected, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        public static ParsedQueryCriteria Parse(string query, string fallbackTypeFilter = null, string fallbackDomainFilter = null)
        {
            var result = new ParsedQueryCriteria
            {
                RawQuery = query,
                TypeFilter = NormalizeType(fallbackTypeFilter),
                DomainFilter = fallbackDomainFilter
            };

            if (string.IsNullOrWhiteSpace(query)) return result;

            string remaining = query;

            if (remaining.IndexOf(':') >= 0)
            {
                for (int i = 0; i < FilterPatterns.Length; i++)
                {
                    var fp = FilterPatterns[i];
                    var match = fp.Regex.Match(remaining);
                    if (match.Success)
                    {
                        string val = match.Groups["quoted"].Success
                            ? match.Groups["quoted"].Value
                            : match.Groups["plain"].Value;

                        switch (fp.Key)
                        {
                            case "type":
                                result.TypeFilter = NormalizeType(val);
                                break;
                            case "description":
                                result.DescriptionFilter = val;
                                break;
                            case "metadata":
                                result.MetadataFilter = val;
                                break;
                            case "usedby":
                                result.UsedByFilter = val;
                                break;
                            case "parentPath":
                                result.ParentPathFilter = val;
                                break;
                            case "parent":
                                result.ParentFilter = val;
                                break;
                            case "name":
                                result.NameFilter = val;
                                break;
                        }

                        remaining = remaining.Remove(match.Index, match.Length);
                    }
                }
            }

            if (remaining.IndexOf('"') < 0)
            {
                var parts = remaining.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    result.FreeTerms.Add(parts[i]);
                }
            }
            else
            {
                var matches = QuotedOrPlainToken.Matches(remaining);
                for (int i = 0; i < matches.Count; i++)
                {
                    var m = matches[i];
                    if (!m.Success) continue;
                    string term = m.Groups["quoted"].Success ? m.Groups["quoted"].Value : m.Groups["plain"].Value;
                    if (!string.IsNullOrWhiteSpace(term))
                    {
                        result.FreeTerms.Add(term.Trim());
                    }
                }
            }

            return result;
        }
    }
}