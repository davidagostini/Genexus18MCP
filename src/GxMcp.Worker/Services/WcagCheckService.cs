using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 99 — genexus_wcag_check. Reads CaptionExpression / Tooltip values
    /// from a WebPanel's WebForm and runs lightweight WCAG-shaped checks. NO
    /// contrast check — that needs rendered colours, out of scope.
    /// Returns <c>{ violations: [{ rule, control, message }] }</c>.
    /// </summary>
    public class WcagCheckService
    {
        private readonly ObjectService _objectService;

        public WcagCheckService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string Check(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return McpResponse.Err(code: "MissingTarget", message: "target is required.");
            }

            string webform;
            try
            {
                webform = _objectService?.ReadObjectSource(target, "WebForm");
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "ReadFailed", message: ex.Message, target: target);
            }

            if (string.IsNullOrEmpty(webform))
            {
                return McpResponse.Ok(target: target, code: "WcagCheckCompleted", result: new JObject
                {
                    ["violations"] = new JArray(),
                    ["note"] = "No WebForm part on this object."
                });
            }

            var violations = AnalyzeWebForm(webform);
            return McpResponse.Ok(target: target, code: "WcagCheckCompleted", result: new JObject
            {
                ["violations"] = violations,
                ["checked"] = new JArray
                {
                    "empty-caption-with-tooltip",
                    "caption-too-long",
                    "html-in-plain-caption"
                }
            });
        }

        /// <summary>
        /// Pure analysis. Accepts either a JSON-wrapped read response (with
        /// <c>source</c>) or raw XML; tolerates both for testability.
        /// </summary>
        public static JArray AnalyzeWebForm(string webformOrJson)
        {
            string xml = ExtractXml(webformOrJson);
            var violations = new JArray();
            if (string.IsNullOrEmpty(xml)) return violations;

            // Walk every element. Each can carry its own Caption/Tooltip attrs.
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch
            {
                // Fall back to regex when the XML is malformed/CDATA-wrapped.
                ScanWithRegex(xml, violations);
                return violations;
            }

            foreach (var el in doc.Descendants())
            {
                string caption = AttrAny(el, "Caption", "CaptionExpression");
                string tooltip = AttrAny(el, "Tooltip", "ToolTip");
                string controlName = AttrAny(el, "Name", "ControlName") ?? el.Name.LocalName;

                // Rule A: empty caption + non-empty tooltip → likely missing accessible name.
                if (string.IsNullOrWhiteSpace(caption) && !string.IsNullOrWhiteSpace(tooltip))
                {
                    violations.Add(new JObject
                    {
                        ["rule"] = "empty-caption-with-tooltip",
                        ["control"] = controlName,
                        ["message"] = "Control has a Tooltip but no Caption; screen readers may miss the accessible name."
                    });
                }

                // Rule B: caption too long.
                if (!string.IsNullOrEmpty(caption) && StripQuotes(caption).Length > 80)
                {
                    violations.Add(new JObject
                    {
                        ["rule"] = "caption-too-long",
                        ["control"] = controlName,
                        ["message"] = "Caption exceeds 80 chars (" + StripQuotes(caption).Length + ")."
                    });
                }

                // Rule C: HTML embedded in plain-text caption.
                if (!string.IsNullOrEmpty(caption) && LooksLikeHtml(caption))
                {
                    violations.Add(new JObject
                    {
                        ["rule"] = "html-in-plain-caption",
                        ["control"] = controlName,
                        ["message"] = "Caption appears to contain HTML markup; use Format=\"HTML\" or move markup out of Caption."
                    });
                }
            }
            return violations;
        }

        private static string ExtractXml(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            // If the input looks like a read-response envelope, try to pull the source field.
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("{"))
            {
                try
                {
                    var jo = JObject.Parse(raw);
                    var src = jo["source"]?.ToString()
                              ?? jo["content"]?.ToString()
                              ?? jo["WebForm"]?.ToString();
                    if (!string.IsNullOrEmpty(src)) return src;
                }
                catch { }
            }
            return raw;
        }

        private static string AttrAny(XElement el, params string[] names)
        {
            foreach (var n in names)
            {
                var a = el.Attribute(n);
                if (a != null && !string.IsNullOrEmpty(a.Value)) return a.Value;
            }
            return null;
        }

        private static string StripQuotes(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Trim();
            if (t.Length >= 2 && t[0] == '\'' && t[t.Length - 1] == '\'')
                return t.Substring(1, t.Length - 2);
            if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"')
                return t.Substring(1, t.Length - 2);
            return t;
        }

        private static readonly Regex HtmlTagRegex = new Regex(
            @"<\s*[a-zA-Z][a-zA-Z0-9]*(\s[^>]*)?>",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex ElemMatchRegex = new Regex(
            @"<\s*(?<tag>[a-zA-Z][\w:-]*)\b(?<attrs>[^/>]*)/?>",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> AttrRegexCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);

        private static bool LooksLikeHtml(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return HtmlTagRegex.IsMatch(s);
        }

        private static void ScanWithRegex(string xml, JArray violations)
        {
            var elemMatches = ElemMatchRegex.Matches(xml);
            foreach (Match m in elemMatches)
            {
                string attrs = m.Groups["attrs"].Value;
                string caption = AttrRegex(attrs, "Caption", "CaptionExpression");
                string tooltip = AttrRegex(attrs, "Tooltip", "ToolTip");
                string control = AttrRegex(attrs, "Name", "ControlName") ?? m.Groups["tag"].Value;

                if (string.IsNullOrWhiteSpace(caption) && !string.IsNullOrWhiteSpace(tooltip))
                {
                    violations.Add(new JObject
                    {
                        ["rule"] = "empty-caption-with-tooltip",
                        ["control"] = control,
                        ["message"] = "Control has a Tooltip but no Caption."
                    });
                }
                if (!string.IsNullOrEmpty(caption) && StripQuotes(caption).Length > 80)
                {
                    violations.Add(new JObject
                    {
                        ["rule"] = "caption-too-long",
                        ["control"] = control,
                        ["message"] = "Caption exceeds 80 chars."
                    });
                }
                if (!string.IsNullOrEmpty(caption) && LooksLikeHtml(caption))
                {
                    violations.Add(new JObject
                    {
                        ["rule"] = "html-in-plain-caption",
                        ["control"] = control,
                        ["message"] = "Caption appears to contain HTML markup."
                    });
                }
            }
        }

        private static string AttrRegex(string attrs, params string[] names)
        {
            foreach (var n in names)
            {
                var rx = AttrRegexCache.GetOrAdd(n, name => new Regex(name + @"\s*=\s*""(?<v>[^""]*)""", RegexOptions.CultureInvariant | RegexOptions.Compiled));
                var m = rx.Match(attrs);
                if (m.Success && !string.IsNullOrEmpty(m.Groups["v"].Value)) return m.Groups["v"].Value;
            }
            return null;
        }
    }
}
