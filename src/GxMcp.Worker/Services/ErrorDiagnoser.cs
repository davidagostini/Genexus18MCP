using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class AutoFixSuggestion
    {
        public string ErrorCode { get; set; } = string.Empty;
        public string Tool { get; set; } = string.Empty;
        public JObject Arguments { get; set; } = new JObject();
        public string Explanation { get; set; } = string.Empty;
        public double Confidence { get; set; } = 0.9;
    }

    public static class ErrorDiagnoser
    {
        // Multilingual regexes supporting English, Portuguese and Spanish GeneXus logs
        private static readonly Regex Spc0005Regex = new Regex(
            @"(?:spc|src)0005:\s*(?:Variable|Variável)\s*['""]?(&?[A-Za-z0-9_]+)['""]?\s*(?:not defined|n[ãa]o definida|no definida)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Spc0107Regex = new Regex(
            @"(?:spc|src)0107:\s*['""]?([A-Za-z0-9_]+)['""]?\s*(?:is not a Business Component|n[ãa]o é um Business Component|no es un Business Component)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Spc0053Regex = new Regex(
            @"(?:spc|src)(?:0053|0030):\s*(?:Subroutine|Subrotina|Subrutina)\s*['""]?([A-Za-z0-9_]+)['""]?\s*(?:not defined|n[ãa]o definida|no definida)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Spc0011Regex = new Regex(
            @"(?:spc|src)0011:\s*['""]?parm['""]?\s*(?:rule invalid|regra inv[áa]lida|regla inv[áa]lida)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Spc0130Regex = new Regex(
            @"(?:spc|src)0130:\s*(?:Object|Objeto)\s*['""]?([A-Za-z0-9_]+)['""]?\s*(?:does not exist|n[ãa]o existe|no existe)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Spc0038Regex = new Regex(
            @"(?:spc|src)0038:\s*(?:Attribute|Atributo)\s*['""]?([A-Za-z0-9_]+)['""]?\s*(?:is not in table|n[ãa]o est[áa] na tabela|no est[áa] en la tabla|is not in the base table)\s*['""]?([A-Za-z0-9_]+)?['""]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Spc0026Regex = new Regex(
            @"(?:spc|src)0026:\s*(?:Syntax error|Erro de sintaxe|Error de sintaxis)(?:\s*:\s*(.*))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Spc0089Regex = new Regex(
            @"(?:spc|src)0089:\s*(?:Expressão inválida: esperando a definição da sub-rotina|Invalid expression: expecting subroutine definition|Expresión inválida)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Spc0216Regex = new Regex(
            @"(?:spc|src)0216:\s*(?:Attribute|Atributo)\s*['""]?([A-Za-z0-9_]+)['""]?\s*(?:not defined|n[ãa]o definido|no definido)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<AutoFixSuggestion> Diagnose(IEnumerable<string> errorLines, string defaultObjectName = null, int maxSuggestions = 20)
        {
            var results = new List<AutoFixSuggestion>();
            if (errorLines == null) return results;

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawLine in errorLines)
            {
                if (results.Count >= maxSuggestions) break;
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                string line = rawLine.Trim();

                // 1. spc0005: Variable not defined
                var m0005 = Spc0005Regex.Match(line);
                if (m0005.Success)
                {
                    string varName = m0005.Groups[1].Value;
                    if (!varName.StartsWith("&")) varName = "&" + varName;
                    string key = $"spc0005_{varName}";
                    if (seenKeys.Add(key))
                    {
                        var args = new JObject
                        {
                            ["action"] = "add",
                            ["name"] = varName,
                            ["type"] = InferTypeFromVarName(varName)
                        };
                        if (!string.IsNullOrEmpty(defaultObjectName))
                        {
                            args["object"] = defaultObjectName;
                        }
                        results.Add(new AutoFixSuggestion
                        {
                            ErrorCode = "spc0005",
                            Tool = "genexus_variable",
                            Arguments = args,
                            Explanation = $"Variable '{varName}' is not defined. Add it using genexus_variable.",
                            Confidence = 0.95
                        });
                    }
                    continue;
                }

                // 2. spc0107: Object is not a Business Component
                var m0107 = Spc0107Regex.Match(line);
                if (m0107.Success)
                {
                    string objName = m0107.Groups[1].Value;
                    string key = $"spc0107_{objName}";
                    if (seenKeys.Add(key))
                    {
                        var args = new JObject
                        {
                            ["action"] = "set",
                            ["name"] = objName,
                            ["propertyName"] = "BusinessComponent",
                            ["value"] = "True"
                        };
                        results.Add(new AutoFixSuggestion
                        {
                            ErrorCode = "spc0107",
                            Tool = "genexus_properties",
                            Arguments = args,
                            Explanation = $"'{objName}' is referenced as a Business Component but the property is not enabled. Enable it with genexus_properties.",
                            Confidence = 0.98
                        });
                    }
                    continue;
                }

                // 3. spc0053: Subroutine not defined
                var m0053 = Spc0053Regex.Match(line);
                if (m0053.Success)
                {
                    string subName = m0053.Groups[1].Value;
                    string key = $"spc0053_{subName}";
                    if (seenKeys.Add(key))
                    {
                        var args = new JObject();
                        if (!string.IsNullOrEmpty(defaultObjectName))
                        {
                            args["name"] = defaultObjectName;
                            args["part"] = "Source";
                        }
                        results.Add(new AutoFixSuggestion
                        {
                            ErrorCode = "spc0053",
                            Tool = "genexus_edit",
                            Arguments = args,
                            Explanation = $"Subroutine '{subName}' was called but not implemented. Add `Sub '{subName}' ... EndSub` in Source or Events.",
                            Confidence = 0.9
                        });
                    }
                    continue;
                }

                // 4. spc0011: Parm rule invalid
                var m0011 = Spc0011Regex.Match(line);
                if (m0011.Success)
                {
                    string key = "spc0011_parm";
                    if (seenKeys.Add(key))
                    {
                        var args = new JObject
                        {
                            ["part"] = "Rules"
                        };
                        if (!string.IsNullOrEmpty(defaultObjectName))
                        {
                            args["name"] = defaultObjectName;
                        }
                        results.Add(new AutoFixSuggestion
                        {
                            ErrorCode = "spc0011",
                            Tool = "genexus_read",
                            Arguments = args,
                            Explanation = "The 'parm' rule is invalid or has mismatched parameters. Inspect the Rules part with genexus_read.",
                            Confidence = 0.85
                        });
                    }
                    continue;
                }

                // 5. spc0130: Object does not exist
                var m0130 = Spc0130Regex.Match(line);
                if (m0130.Success)
                {
                    string missingObj = m0130.Groups[1].Value;
                    string key = $"spc0130_{missingObj}";
                    if (seenKeys.Add(key))
                    {
                        results.Add(new AutoFixSuggestion
                        {
                            ErrorCode = "spc0130",
                            Tool = "genexus_create",
                            Arguments = new JObject
                            {
                                ["name"] = missingObj,
                                ["type"] = "Procedure"
                            },
                            Explanation = $"Referenced object '{missingObj}' does not exist in the KB. Create it with genexus_create.",
                            Confidence = 0.8
                        });
                    }
                    continue;
                }

                // 6. spc0038: Attribute is not in table
                var m0038 = Spc0038Regex.Match(line);
                if (m0038.Success)
                {
                    string attrName = m0038.Groups[1].Value;
                    string tableName = m0038.Groups[2].Value;
                    string key = $"spc0038_{attrName}_{tableName}";
                    if (seenKeys.Add(key))
                    {
                        results.Add(new AutoFixSuggestion
                        {
                            ErrorCode = "spc0038",
                            Tool = "genexus_structure",
                            Arguments = new JObject
                            {
                                ["action"] = "get_logic",
                                ["name"] = tableName
                            },
                            Explanation = $"Attribute '{attrName}' is not in table '{tableName}'. Inspect table structure or add attribute to transaction.",
                            Confidence = 0.85
                        });
                    }
                    continue;
                }

                // 7. spc0026: Syntax error
                var m0026 = Spc0026Regex.Match(line);
                if (m0026.Success)
                {
                    string key = $"spc0026_syntax_{defaultObjectName ?? "source"}";
                    if (seenKeys.Add(key))
                    {
                        var args = new JObject();
                        if (!string.IsNullOrEmpty(defaultObjectName))
                        {
                            args["name"] = defaultObjectName;
                        }
                        results.Add(new AutoFixSuggestion
                        {
                            ErrorCode = "spc0026",
                            Tool = "genexus_read",
                            Arguments = args,
                            Explanation = "Syntax error encountered during specification. Read object source/rules with genexus_read to inspect and fix syntax.",
                            Confidence = 0.8
                        });
                    }
                    continue;
                }

                // 8. src0089: Invalid expression: expecting subroutine definition (ordering error)
                var m0089 = Spc0089Regex.Match(line);
                if (m0089.Success)
                {
                    string key = $"src0089_sub_order_{defaultObjectName ?? "source"}";
                    if (seenKeys.Add(key))
                    {
                        var args = new JObject();
                        if (!string.IsNullOrEmpty(defaultObjectName))
                        {
                            args["name"] = defaultObjectName;
                            args["part"] = "Source";
                        }
                        results.Add(new AutoFixSuggestion
                        {
                            ErrorCode = "src0089",
                            Tool = "genexus_edit",
                            Arguments = args,
                            Explanation = "GeneXus requires all top-level procedural statements (such as `Do 'Sub'`) to precede all subroutine (`Sub ... EndSub`) definitions.",
                            Confidence = 0.95
                        });
                    }
                    continue;
                }

                // 9. src0216: Attribute not defined
                var m0216 = Spc0216Regex.Match(line);
                if (m0216.Success)
                {
                    string attrName = m0216.Groups[1].Value;
                    string key = $"src0216_{attrName}";
                    if (seenKeys.Add(key))
                    {
                        results.Add(new AutoFixSuggestion
                        {
                            ErrorCode = "src0216",
                            Tool = "genexus_variable",
                            Arguments = new JObject
                            {
                                ["action"] = "add",
                                ["name"] = "&" + attrName,
                                ["type"] = InferTypeFromVarName(attrName)
                            },
                            Explanation = $"Attribute '{attrName}' is not defined. If this was intended as a local variable, declare it with genexus_variable.",
                            Confidence = 0.85
                        });
                    }
                    continue;
                }
            }

            return results;
        }

        private static string InferTypeFromVarName(string varName)
        {
            string clean = varName.TrimStart('&').ToLowerInvariant();
            if (clean.StartsWith("is") || clean.StartsWith("has") || clean.EndsWith("ok") || clean == "success")
                return "Boolean";
            if (clean.EndsWith("id") || clean.EndsWith("num") || clean.EndsWith("qty") || clean.EndsWith("count") || clean == "i" || clean == "j")
                return "Numeric";
            if (clean.EndsWith("date"))
                return "Date";
            if (clean.EndsWith("datetime") || clean.EndsWith("time") || clean.EndsWith("dtime"))
                return "DateTime";
            if (clean.EndsWith("msg") || clean.EndsWith("message") || clean.EndsWith("text") || clean.EndsWith("name") || clean.EndsWith("desc") || clean.EndsWith("code"))
                return "Character";
            if (clean.EndsWith("sdt") || clean.EndsWith("item") || clean.EndsWith("list"))
                return "SDT";
            return "Character";
        }
    }
}
