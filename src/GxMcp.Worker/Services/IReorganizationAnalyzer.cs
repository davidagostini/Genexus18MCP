using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Services
{
    public class DdlStatement
    {
        public string Kind { get; set; } = string.Empty;
        public string Table { get; set; } = string.Empty;
        public string Column { get; set; } = string.Empty;
        public string Sql { get; set; } = string.Empty;
        public bool IsDestructive { get; set; }
    }

    public class ReorganizationPlan
    {
        public List<DdlStatement> Statements { get; set; } = new List<DdlStatement>();
        public List<string> AffectedTables { get; set; } = new List<string>();
        public List<string> AffectedColumns { get; set; } = new List<string>();
        public int DestructiveCount => Statements.Count(s => s.IsDestructive);
    }

    public interface IReorganizationAnalyzer
    {
        ReorganizationPlan AnalyzeSqlScript(string sqlContent);
        string ClassifyStatement(string sqlStatement, out bool isDestructive);
    }

    public class ReorganizationAnalyzer : IReorganizationAnalyzer
    {
        private static readonly Regex StatementSplit = new Regex(@";\s*(?:\r?\n|$)", RegexOptions.Compiled);
        private static readonly Regex TableName = new Regex(@"\b(?:TABLE|ON|INTO)\s+(?:\[[^\]]+\]\.)?(?<name>\[[^\]]+\]|[\w.$]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ColumnName = new Regex(@"\b(?:ADD|ALTER|DROP)\s+(?:COLUMN\s+)?(?<name>\[[^\]]+\]|[\w$]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public ReorganizationPlan AnalyzeSqlScript(string sqlContent)
        {
            var plan = new ReorganizationPlan();
            if (string.IsNullOrWhiteSpace(sqlContent)) return plan;

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var rawStatements = StatementSplit.Split(sqlContent);
            foreach (var raw in rawStatements)
            {
                string statement = raw.Trim();
                if (string.IsNullOrEmpty(statement)) continue;

                string kind = ClassifyStatement(statement, out bool isDestructive);

                string table = "";
                var mTable = TableName.Match(statement);
                if (mTable.Success)
                {
                    table = mTable.Groups["name"].Value.Trim('[', ']');
                    tables.Add(table);
                }

                string column = "";
                var mCol = ColumnName.Match(statement);
                if (mCol.Success)
                {
                    column = mCol.Groups["name"].Value.Trim('[', ']');
                    columns.Add(column);
                }

                plan.Statements.Add(new DdlStatement
                {
                    Kind = kind,
                    Table = table,
                    Column = column,
                    Sql = statement + ";",
                    IsDestructive = isDestructive
                });
            }

            plan.AffectedTables = tables.OrderBy(t => t).ToList();
            plan.AffectedColumns = columns.OrderBy(c => c).ToList();
            return plan;
        }

        public string ClassifyStatement(string sqlStatement, out bool isDestructive)
        {
            isDestructive = false;
            if (string.IsNullOrWhiteSpace(sqlStatement)) return "unknown";

            string s = Regex.Replace(sqlStatement, @"\s+", " ").Trim().ToUpperInvariant();

            if (s.StartsWith("CREATE TABLE ")) return "create_table";
            if (s.StartsWith("DROP TABLE ")) { isDestructive = true; return "drop_table"; }
            if (s.StartsWith("CREATE INDEX ") || s.StartsWith("CREATE UNIQUE INDEX ")) return "create_index";
            if (s.StartsWith("DROP INDEX ")) return "drop_index";
            if (s.StartsWith("ALTER TABLE "))
            {
                if (s.Contains(" DROP COLUMN ") || s.Contains(" DROP ")) { isDestructive = true; return "drop_column"; }
                if (s.Contains(" ADD COLUMN ") || s.Contains(" ADD ")) return "add_column";
                if (s.Contains(" ALTER COLUMN ") || s.Contains(" MODIFY ")) { isDestructive = true; return "alter_column"; }
                return "alter_table";
            }

            return "other";
        }
    }
}
