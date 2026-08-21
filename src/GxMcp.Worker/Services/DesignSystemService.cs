using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Helpers;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using DSObject = Artech.Genexus.Common.Objects.DesignSystem;

namespace GxMcp.Worker.Services
{
    public class DsoValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; }

        public DsoValidationResult()
        {
            IsValid = true;
            Errors = new List<string>();
        }
    }

    /// <summary>
    /// genexus_layout action=design_system — read a Design System Object's (DSO) tokens,
    /// theme classes, images and referenced DSOs, over <c>DesignSystemHelper</c>.
    /// Provides token and class parsing and DSO syntax validation.
    /// </summary>
    public class DesignSystemService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;

        public DesignSystemService(KbService kb, ObjectService objects)
        {
            _kb = kb;
            _objects = objects;
        }

        public string Run(JObject args)
        {
            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            string action = args?["action"]?.ToString()?.ToLowerInvariant();
            if (action == "validate")
            {
                string source = args?["source"]?.ToString() ?? args?["content"]?.ToString();
                var validation = ValidateDso(source);
                return McpResponse.Ok(
                    code: "DsoValidated",
                    result: new JObject
                    {
                        ["isValid"] = validation.IsValid,
                        ["errors"] = new JArray(validation.Errors)
                    });
            }

            string name = args?["name"]?.ToString();
            DSObject dso = null;

            if (!string.IsNullOrWhiteSpace(name))
            {
                try { dso = _objects?.FindObject(name, "DesignSystem") as DSObject; } catch { }
                if (dso == null)
                    return McpResponse.Err("ObjectNotFound", "Design System Object '" + name + "' not found.", "Check the name (genexus_query type:DesignSystem), or omit name to use the first DSO.", target: name);
            }
            else
            {
                // Fast path: search index bucket
                try
                {
                    var index = _objects?.GetLoadedIndexOrNull();
                    if (index?.TypeIndex != null && index.Objects != null
                        && index.TypeIndex.TryGetValue("DesignSystem", out var dsKeys))
                    {
                        string firstKey = null;
                        lock (dsKeys) { foreach (var k in dsKeys) { firstKey = k; break; } }
                        if (firstKey != null && index.Objects.TryGetValue(firstKey, out var entry)
                            && !string.IsNullOrEmpty(entry?.Name))
                        {
                            dso = _objects.FindObject(entry.Name, "DesignSystem") as DSObject;
                            if (dso != null) name = entry.Name;
                        }
                    }
                }
                catch { }

                if (dso == null)
                {
                    try
                    {
                        foreach (KBObject o in model.Objects.GetAll())
                        {
                            if (string.Equals(o?.TypeDescriptor?.Name, "DesignSystem", StringComparison.OrdinalIgnoreCase))
                            { dso = o as DSObject; if (dso != null) { name = o.Name; break; } }
                        }
                    }
                    catch { }
                }

                if (dso == null)
                    return McpResponse.Err("NoDesignSystem", "This KB has no Design System Object.", "DSOs are created in the GeneXus IDE; nothing to read.");
            }

            try
            {
                var helper = new DesignSystemHelper(dso);

                var tokens = new JObject();
                try
                {
                    var byGroup = helper.GetTokensNames();
                    if (byGroup != null)
                        foreach (var kv in byGroup)
                            tokens[kv.Key] = ToArray(kv.Value);
                }
                catch { }

                return McpResponse.Ok(
                    code: "DesignSystemRetrieved",
                    result: new JObject
                    {
                        ["designSystem"] = name,
                        ["tokenGroups"] = tokens,
                        ["classes"] = ToArray(SafeList(() => helper.GetClassesNames())),
                        ["images"] = ToArray(SafeList(() => helper.GetAllImagesNames())),
                        ["referencedDSOs"] = ToArray(SafeList(() => helper.GetAllDSOsNames())),
                        ["source"] = "sdk:DesignSystemHelper"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err("DesignSystemReadFailed", ex.Message, "Check the worker log for the full stack trace.");
            }
        }

        public static Dictionary<string, JObject> ParseDsoTokens(string dsoTokens)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(dsoTokens)) return result;

            var groupRegex = new Regex(@"#([A-Za-z0-9_-]+)\s*\{([^}]+)\}", RegexOptions.Multiline);
            var itemRegex = new Regex(@"([A-Za-z0-9_-]+)\s*:\s*([^;]+);", RegexOptions.Multiline);

            foreach (Match gMatch in groupRegex.Matches(dsoTokens))
            {
                string groupName = gMatch.Groups[1].Value.Trim();
                string groupBody = gMatch.Groups[2].Value;

                var groupObj = new JObject();
                foreach (Match iMatch in itemRegex.Matches(groupBody))
                {
                    string key = iMatch.Groups[1].Value.Trim();
                    string val = iMatch.Groups[2].Value.Trim();
                    groupObj[key] = val;
                }

                result[groupName] = groupObj;
            }

            return result;
        }

        public static Dictionary<string, JObject> ParseDsoClasses(string dsoStyles)
        {
            var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(dsoStyles)) return result;

            // Matches .ClassName and variants like .Button:hover or .Card.Active
            var classRegex = new Regex(@"\.([A-Za-z0-9_:-]+)\s*\{([^}]+)\}", RegexOptions.Multiline);
            var propRegex = new Regex(@"([A-Za-z0-9_-]+)\s*:\s*([^;]+);", RegexOptions.Multiline);

            foreach (Match cMatch in classRegex.Matches(dsoStyles))
            {
                string className = cMatch.Groups[1].Value.Trim();
                string classBody = cMatch.Groups[2].Value;

                var classObj = new JObject();
                foreach (Match pMatch in propRegex.Matches(classBody))
                {
                    string key = pMatch.Groups[1].Value.Trim();
                    string val = pMatch.Groups[2].Value.Trim();
                    classObj[key] = val;
                }

                result[className] = classObj;
            }

            return result;
        }

        public static DsoValidationResult ValidateDso(string dsoCombined)
        {
            var res = new DsoValidationResult();
            if (string.IsNullOrWhiteSpace(dsoCombined))
            {
                res.IsValid = false;
                res.Errors.Add("Design System source is empty.");
                return res;
            }

            // Strip comments and string literals before validating brace structure to avoid false positives
            string sanitized = StripCommentsAndStrings(dsoCombined);

            int openBraces = 0;
            for (int i = 0; i < sanitized.Length; i++)
            {
                char c = sanitized[i];
                if (c == '{') openBraces++;
                else if (c == '}') openBraces--;

                if (openBraces < 0)
                {
                    res.IsValid = false;
                    res.Errors.Add($"Closing brace '}}' without matching opening brace.");
                    return res;
                }
            }

            if (openBraces != 0)
            {
                res.IsValid = false;
                res.Errors.Add($"Mismatched braces: {openBraces} opening brace(s) '{{' were not closed.");
            }

            return res;
        }

        private static string StripCommentsAndStrings(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            // Replace block comments /* ... */ with space
            string withoutBlockComments = Regex.Replace(input, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            // Replace line comments // ... with newline
            string withoutLineComments = Regex.Replace(withoutBlockComments, @"//.*?$", "", RegexOptions.Multiline);
            // Replace quoted strings "..." and '...' with ""
            string withoutStrings = Regex.Replace(withoutLineComments, @"""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'", "\"\"", RegexOptions.Singleline);

            return withoutStrings;
        }

        private static IEnumerable SafeList(Func<IEnumerable> f) { try { return f(); } catch { return null; } }

        private static JArray ToArray(IEnumerable items)
        {
            var arr = new JArray();
            if (items != null) foreach (var i in items) if (i != null) arr.Add(i.ToString());
            return arr;
        }
    }
}
