using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Services
{
    public class ParameterDescriptor
    {
        public string Name { get; set; } = string.Empty;
        public string Accessor { get; set; } = "in";
        public string Type { get; set; } = "Unknown";
        public string RawToken { get; set; } = string.Empty;
    }

    public class SignatureDescriptor
    {
        public string ParmRule { get; set; } = string.Empty;
        public List<ParameterDescriptor> Parameters { get; set; } = new List<ParameterDescriptor>();
        public List<string> OutgoingCalls { get; set; } = new List<string>();
    }

    /// <summary>
    /// Pure domain parser for GeneXus parm(...) rules, accessor prefixes,
    /// parameter directions, and call/udp/submit statements.
    /// Operates purely on source and rule text without requiring SDK objects.
    /// </summary>
    public static class GeneXusSignatureParser
    {
        private static readonly Regex ParmRegex = new Regex(@"parm\s*\((.*)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CallRegex = new Regex(@"\b(?:call|udp|submit)\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static SignatureDescriptor Parse(string rulesText, string sourceText = null)
        {
            var result = new SignatureDescriptor();

            // 1. Extract and parse parm rule
            if (!string.IsNullOrWhiteSpace(rulesText))
            {
                string ruleLine = rulesText
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("parm(", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(ruleLine))
                {
                    result.ParmRule = ruleLine;
                    var match = ParmRegex.Match(ruleLine);
                    if (match.Success)
                    {
                        var content = match.Groups[1].Value;
                        var parts = content.Split(',');
                        foreach (var rawPart in parts)
                        {
                            var trimmed = rawPart.Trim();
                            if (string.IsNullOrEmpty(trimmed)) continue;

                            var p = new ParameterDescriptor { RawToken = trimmed, Accessor = "in", Type = "Unknown" };

                            if (trimmed.StartsWith("inout:", StringComparison.OrdinalIgnoreCase))
                            {
                                p.Accessor = "inout";
                                p.Name = trimmed.Substring(6).Trim();
                            }
                            else if (trimmed.StartsWith("in:", StringComparison.OrdinalIgnoreCase))
                            {
                                p.Accessor = "in";
                                p.Name = trimmed.Substring(3).Trim();
                            }
                            else if (trimmed.StartsWith("out:", StringComparison.OrdinalIgnoreCase))
                            {
                                p.Accessor = "out";
                                p.Name = trimmed.Substring(4).Trim();
                            }
                            else
                            {
                                p.Name = trimmed;
                            }

                            if (p.Name.StartsWith("&")) p.Name = p.Name.Substring(1);
                            result.Parameters.Add(p);
                        }
                    }
                }
            }

            // 2. Extract outgoing calls from source
            string combinedSource = (rulesText ?? "") + "\n" + (sourceText ?? "");
            var callMatches = CallRegex.Matches(combinedSource);
            var seenCalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in callMatches)
            {
                if (m.Success && m.Groups.Count > 1)
                {
                    string target = m.Groups[1].Value;
                    if (seenCalls.Add(target))
                    {
                        result.OutgoingCalls.Add(target);
                    }
                }
            }

            return result;
        }
    }
}
