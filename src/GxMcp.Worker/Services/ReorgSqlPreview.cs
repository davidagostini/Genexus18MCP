using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    internal static class ReorgSqlPreview
    {
        private static readonly Regex StatementSplit = new Regex(@";\s*(?:\r?\n|$)", RegexOptions.Compiled);
        private static readonly Regex TableName = new Regex(@"\b(?:TABLE|ON)\s+(?:\[[^\]]+\]\.)?(?<name>\[[^\]]+\]|[\w.$]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ColumnName = new Regex(@"\b(?:ADD|ALTER|DROP)\s+(?:COLUMN\s+)?(?<name>\[[^\]]+\]|[\w$]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string FindLatestArtifact(string kbPath, DateTime notBeforeUtc, out bool currentRun)
        {
            currentRun = false;
            if (string.IsNullOrWhiteSpace(kbPath) || !Directory.Exists(kbPath)) return null;
            try
            {
                var candidates = Directory.EnumerateFiles(kbPath, "*.sql", SearchOption.AllDirectories)
                    .Take(10000)
                    .Where(p => Path.GetFileName(p).IndexOf("reorg", StringComparison.OrdinalIgnoreCase) >= 0
                             || Path.GetFileName(p).IndexOf("impact", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                FileInfo latest = candidates.FirstOrDefault();
                if (latest == null) return null;
                currentRun = latest.LastWriteTimeUtc >= notBeforeUtc.AddSeconds(-2);
                return latest.FullName;
            }
            catch { return null; }
        }

        public static JObject Parse(string sql, bool effective)
        {
            var statements = new JArray();
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexes = new JArray();
            var destructive = new JArray();

            foreach (string raw in StatementSplit.Split(sql ?? string.Empty))
            {
                string statement = raw.Trim();
                if (statement.Length == 0) continue;
                string kind = Classify(statement);
                Match tm = TableName.Match(statement);
                string table = tm.Success ? Unquote(tm.Groups["name"].Value) : null;
                if (!string.IsNullOrEmpty(table)) tables.Add(table);
                string column = null;
                if (kind.IndexOf("column", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Match cm = ColumnName.Match(statement);
                    column = cm.Success ? Unquote(cm.Groups["name"].Value) : null;
                    if (!string.IsNullOrEmpty(column)) columns.Add((table ?? "?") + "." + column);
                }
                bool isDestructive = Regex.IsMatch(statement, @"\b(DROP|TRUNCATE)\b", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(statement, @"\bALTER\b[\s\S]*\bNOT\s+NULL\b", RegexOptions.IgnoreCase);
                var item = new JObject
                {
                    ["kind"] = kind,
                    ["table"] = table,
                    ["column"] = column,
                    ["sql"] = statement + ";",
                    ["destructive"] = isDestructive
                };
                statements.Add(item);
                if (kind.IndexOf("index", StringComparison.OrdinalIgnoreCase) >= 0) indexes.Add(item.DeepClone());
                if (isDestructive) destructive.Add(item.DeepClone());
            }

            return new JObject
            {
                ["ddlEffective"] = effective,
                ["ddl"] = statements,
                ["affectedTables"] = new JArray(tables.OrderBy(x => x)),
                ["affectedColumns"] = new JArray(columns.OrderBy(x => x)),
                ["indexes"] = indexes,
                ["destructiveConversions"] = destructive,
                ["summary"] = new JObject
                {
                    ["statements"] = statements.Count,
                    ["tables"] = tables.Count,
                    ["columns"] = columns.Count,
                    ["indexes"] = indexes.Count,
                    ["destructive"] = destructive.Count
                }
            };
        }

        private static string Classify(string sql)
        {
            string s = Regex.Replace(sql, @"\s+", " ").Trim().ToUpperInvariant();
            if (s.StartsWith("CREATE TABLE ")) return "create_table";
            if (s.StartsWith("DROP TABLE ")) return "drop_table";
            if (s.StartsWith("CREATE INDEX ") || s.StartsWith("CREATE UNIQUE INDEX ")) return "create_index";
            if (s.StartsWith("DROP INDEX ")) return "drop_index";
            if (s.StartsWith("ALTER TABLE ") && s.Contains(" DROP ")) return "drop_column";
            if (s.StartsWith("ALTER TABLE ") && s.Contains(" ADD ")) return "add_column";
            if (s.StartsWith("ALTER TABLE ")) return "alter_column";
            if (s.StartsWith("RENAME ") || s.Contains("SP_RENAME")) return "rename";
            return "sql";
        }

        private static string Unquote(string value) => (value ?? string.Empty).Trim().Trim('[', ']', '`', '"');
    }
}
