using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Models
{
    /// <summary>
    /// Represents a structured filter condition extracted from a GeneXus navigation block.
    /// </summary>
    public class NavigationFilter
    {
        public string Element { get; set; }
        public string Expression { get; set; }
        public string Attribute { get; set; }
        public string Op { get; set; }
        public string Value { get; set; }

        public JObject ToJson()
        {
            var obj = new JObject();
            if (!string.IsNullOrEmpty(Element)) obj["element"] = Element;
            if (Expression != null) obj["expression"] = Expression;
            if (!string.IsNullOrEmpty(Attribute)) obj["attribute"] = Attribute;
            if (!string.IsNullOrEmpty(Op)) obj["op"] = Op;
            if (!string.IsNullOrEmpty(Value)) obj["value"] = Value;
            return obj;
        }

        public static NavigationFilter FromJson(JObject json)
        {
            if (json == null) return null;
            return new NavigationFilter
            {
                Element = (string)json["element"],
                Expression = (string)json["expression"],
                Attribute = (string)json["attribute"],
                Op = (string)json["op"],
                Value = (string)json["value"]
            };
        }
    }

    /// <summary>
    /// Represents a single data navigation level (For Each or data table access).
    /// </summary>
    public class NavigationLevel
    {
        public int? Number { get; set; }
        public string Type { get; set; }
        public int? Line { get; set; }
        public string BaseTable { get; set; }
        public string BaseTableDescription { get; set; }
        public string Index { get; set; }
        public List<string> Order { get; set; } = new List<string>();
        public bool IsOptimized { get; set; }
        public List<NavigationFilter> Filters { get; set; } = new List<NavigationFilter>();

        public JObject ToJson()
        {
            var obj = new JObject
            {
                ["number"] = Number,
                ["type"] = Type,
                ["line"] = Line
            };

            if (BaseTable != null) obj["baseTable"] = BaseTable;
            if (BaseTableDescription != null) obj["baseTableDescription"] = BaseTableDescription;
            obj["index"] = Index;

            var orderArr = new JArray();
            foreach (var o in Order)
            {
                if (!string.IsNullOrEmpty(o)) orderArr.Add(o);
            }
            obj["order"] = orderArr;
            obj["isOptimized"] = IsOptimized;

            var filterArr = new JArray();
            foreach (var f in Filters)
            {
                if (f != null) filterArr.Add(f.ToJson());
            }
            obj["filters"] = filterArr;

            return obj;
        }

        public static NavigationLevel FromJson(JObject json)
        {
            if (json == null) return null;
            var level = new NavigationLevel
            {
                Number = json["number"]?.ToObject<int?>(),
                Type = (string)json["type"],
                Line = json["line"]?.ToObject<int?>(),
                BaseTable = (string)json["baseTable"],
                BaseTableDescription = (string)json["baseTableDescription"],
                Index = (string)json["index"],
                IsOptimized = json["isOptimized"]?.ToObject<bool>() ?? false
            };

            if (json["order"] is JArray orderArr)
            {
                foreach (var item in orderArr)
                {
                    string col = (string)item;
                    if (!string.IsNullOrEmpty(col)) level.Order.Add(col);
                }
            }

            if (json["filters"] is JArray filterArr)
            {
                foreach (var item in filterArr.OfType<JObject>())
                {
                    var f = NavigationFilter.FromJson(item);
                    if (f != null) level.Filters.Add(f);
                }
            }

            return level;
        }
    }

    /// <summary>
    /// Rich domain model representing a parsed GeneXus Navigation report.
    /// Provides direct in-memory SQL generation and JSON projection.
    /// </summary>
    public class NavigationReport
    {
        public string TargetName { get; set; }
        public string Status { get; set; } = "OK";
        public string Message { get; set; }
        public string Hint { get; set; }
        public List<NavigationLevel> Levels { get; set; } = new List<NavigationLevel>();
        public List<string> Warnings { get; set; } = new List<string>();

        public bool IsError => string.Equals(Status, "Error", StringComparison.OrdinalIgnoreCase);

        public static NavigationReport Error(string targetName, string message)
        {
            return new NavigationReport
            {
                TargetName = targetName,
                Status = "Error",
                Message = message
            };
        }

        public JObject ToJson()
        {
            if (IsError)
            {
                return new JObject
                {
                    ["status"] = "Error",
                    ["message"] = Message ?? string.Empty
                };
            }

            var result = new JObject
            {
                ["name"] = TargetName ?? string.Empty
            };

            var levelsArr = new JArray();
            foreach (var l in Levels)
            {
                if (l != null) levelsArr.Add(l.ToJson());
            }
            result["levels"] = levelsArr;

            var warnArr = new JArray();
            foreach (var w in Warnings)
            {
                if (!string.IsNullOrEmpty(w)) warnArr.Add(w);
            }
            result["warnings"] = warnArr;

            if (Levels.Count == 0)
            {
                result["status"] = "NoNavigationBlocks";
                result["hint"] = Hint ?? "Object has no For Each / data-bound navigation blocks. Use mode=summary or mode=data_context for variable/call analysis.";
            }
            else
            {
                result["status"] = Status ?? "OK";
            }

            return result;
        }

        public static NavigationReport FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Error(string.Empty, "Empty JSON");
            try
            {
                var obj = JObject.Parse(json);
                if (string.Equals((string)obj["status"], "Error", StringComparison.OrdinalIgnoreCase))
                {
                    return new NavigationReport
                    {
                        Status = "Error",
                        Message = (string)obj["message"]
                    };
                }

                var report = new NavigationReport
                {
                    TargetName = (string)obj["name"],
                    Status = (string)obj["status"] ?? "OK",
                    Hint = (string)obj["hint"]
                };

                if (obj["levels"] is JArray levelsArr)
                {
                    foreach (var item in levelsArr.OfType<JObject>())
                    {
                        var level = NavigationLevel.FromJson(item);
                        if (level != null) report.Levels.Add(level);
                    }
                }

                if (obj["warnings"] is JArray warnArr)
                {
                    foreach (var item in warnArr)
                    {
                        string w = (string)item;
                        if (!string.IsNullOrEmpty(w)) report.Warnings.Add(w);
                    }
                }

                return report;
            }
            catch (Exception ex)
            {
                return Error(string.Empty, ex.Message);
            }
        }

        /// <summary>
        /// Generates SQL queries and metadata directly from this in-memory navigation report.
        /// </summary>
        public JObject GenerateSql(int? levelNumber = null)
        {
            if (IsError) return ToJson();

            var queries = new JArray();
            var sqlWarnings = new JArray();

            foreach (var l in Levels)
            {
                int num = l.Number ?? 0;
                if (levelNumber.HasValue && num != levelNumber.Value) continue;

                string baseTable = l.BaseTable;
                if (string.IsNullOrEmpty(baseTable))
                {
                    sqlWarnings.Add($"Level {num}: no base table");
                    continue;
                }

                var (where, parms, levelWarnings, structuredFilters) = BuildWhere(l, num);
                foreach (var w in levelWarnings) sqlWarnings.Add(w);

                var sql = new StringBuilder();
                sql.Append("SELECT * FROM ").Append(baseTable);
                if (!string.IsNullOrEmpty(where)) sql.Append(" WHERE ").Append(where);

                if (l.Order != null && l.Order.Count > 0)
                {
                    var orderCols = l.Order.Where(s => !string.IsNullOrWhiteSpace(s));
                    if (orderCols.Any())
                        sql.Append(" ORDER BY ").Append(string.Join(", ", orderCols));
                }

                var parmsArr = new JArray();
                foreach (var p in parms) parmsArr.Add(p);

                var q = new JObject
                {
                    ["level"] = num,
                    ["baseTable"] = baseTable,
                    ["indexUsed"] = l.Index,
                    ["sql"] = sql.ToString(),
                    ["parametersExpected"] = parmsArr
                };
                if (structuredFilters != null && structuredFilters.Count > 0)
                    q["filters"] = structuredFilters;
                queries.Add(q);
            }

            return new JObject
            {
                ["name"] = TargetName,
                ["queries"] = queries,
                ["warnings"] = sqlWarnings
            };
        }

        private (string where, List<string> parms, List<string> warnings, JArray structuredFilters) BuildWhere(NavigationLevel level, int levelNum)
        {
            var parms = new List<string>();
            var warnings = new List<string>();
            var structured = new JArray();

            if (level.Filters == null || level.Filters.Count == 0)
            {
                warnings.Add($"Level {levelNum}: OptimizedWhere not surfaced; SQL emitted without filters.");
                return ("", parms, warnings, structured);
            }

            var clauses = new List<string>();
            foreach (var f in level.Filters)
            {
                string attribute = f.Attribute;
                string op = f.Op;
                string value = f.Value;
                string raw = f.Expression;

                string clause = null;
                if (!string.IsNullOrWhiteSpace(attribute) && !string.IsNullOrWhiteSpace(op))
                {
                    string rhs = string.IsNullOrWhiteSpace(value) ? "?" : ReplaceVarsWithBinds(value, parms);
                    clause = $"{attribute} {op} {rhs}";
                    structured.Add(new JObject { ["attribute"] = attribute, ["op"] = op });
                }
                else if (!string.IsNullOrWhiteSpace(raw))
                {
                    clause = ReplaceVarsWithBinds(raw, parms);
                }

                if (!string.IsNullOrWhiteSpace(clause)) clauses.Add(clause);
            }

            return (string.Join(" AND ", clauses), parms, warnings, structured);
        }

        private static string ReplaceVarsWithBinds(string input, List<string> parms)
        {
            return Regex.Replace(input ?? "", @"&(\w+)", m =>
            {
                string name = m.Groups[1].Value;
                if (!parms.Contains(name)) parms.Add(name);
                return ":" + name;
            });
        }
    }
}
