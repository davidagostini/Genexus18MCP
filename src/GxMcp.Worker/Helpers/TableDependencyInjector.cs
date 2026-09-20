using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Services;
using Artech.Genexus.Common;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Helpers
{
    public static class TableDependencyInjector
    {
        private static readonly Regex TablePatternRegex = new Regex(
            @"\b(?:for\s+each|new|delete)\s+(\w+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static void InjectTableDependencies(KBObject obj, string code, SearchIndex index)
        {
            var variablesPart = obj.Parts.Get<VariablesPart>();
            if (variablesPart == null) return;

            var matches = TablePatternRegex.Matches(code);
            var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < matches.Count; i++)
            {
                tableNames.Add(matches[i].Groups[1].Value);
            }

            foreach (var tableName in tableNames)
            {
                var table = FindTable(obj, tableName);
                if (table != null)
                {
                    string proxyVarName = $"gx_proxy_{tableName}";
                    if (!variablesPart.Variables.Any(v => v.Name.Equals(proxyVarName, StringComparison.OrdinalIgnoreCase)))
                    {
                        global::Artech.Genexus.Common.Variable v = new global::Artech.Genexus.Common.Variable(variablesPart);
                        v.Name = proxyVarName;
                        v.Length = 1;
                        variablesPart.Variables.Add(v);
                        Logger.Info($"Injected table dependency proxy: {proxyVarName} for {tableName}");
                    }
                }
            }
        }

        private static Table FindTable(KBObject obj, string name)
        {
            foreach (var result in obj.Model.Objects.GetByName(null, null, name))
            {
                if (result is Table tbl) return tbl;
            }
            return null;
        }
    }
}
