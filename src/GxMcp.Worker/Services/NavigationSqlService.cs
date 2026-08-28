using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Projects navigation levels and conditions into SQL statements and query optimization metadata.
    /// </summary>
    public class NavigationSqlService
    {
        private readonly NavigationService _navigation;
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;

        public NavigationSqlService(NavigationService navigation)
            : this(navigation, null, null)
        {
        }

        public NavigationSqlService(NavigationService navigation, KbService kbService, ObjectService objectService)
        {
            _navigation = navigation;
            _kbService = kbService;
            _objectService = objectService;
        }

        public string Generate(string objectName, int? levelNumber = null)
            => Generate(objectName, levelNumber, includeExecutionPlan: false, includeIndexAdvisor: false);

        public string Generate(string objectName, int? levelNumber, bool includeExecutionPlan, bool includeIndexAdvisor)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectName))
                {
                    return McpResponse.Err(code: "MissingTarget", message: "Missing objectName.", target: objectName);
                }

                var report = _navigation?.GetReport(objectName);
                if (report == null)
                {
                    return McpResponse.Err(code: "ServiceUnavailable", message: $"Navigation service unavailable for '{objectName}'.", target: objectName);
                }

                if (report.IsError)
                {
                    return report.ToJson().ToString(Newtonsoft.Json.Formatting.None);
                }

                var result = report.GenerateSql(levelNumber);
                var queries = result["queries"] as JArray ?? new JArray();

                // Item 34: optional EXPLAIN annotation per query. Always
                // planUnavailable=true here — the worker has no DB connection.
                if (includeExecutionPlan)
                {
                    int dbmsType = TryGetDbmsType();
                    ExecutionPlanFetcher.AttachExecutionPlans(queries, dbmsType);
                    result["dbmsFamily"] = ExecutionPlanFetcher.ResolveDbmsFamily(dbmsType);
                }

                // Item 44: heuristic index advisor.
                if (includeIndexAdvisor)
                {
                    var existing = CollectExistingIndexes(queries);
                    result["indexAdvisor"] = IndexAdvisor.BuildAdvisor(queries, existing);
                }

                return result.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(code: "NavigationSqlFailed", message: ex.Message, target: objectName);
            }
        }

        private int TryGetDbmsType()
        {
            try
            {
                if (_kbService == null) return 0;
                dynamic kb = _kbService.GetKB();
                if (kb == null) return 0;
                dynamic ds = ((dynamic)kb.DesignModel.Environment.TargetModel).DataStore;
                if (ds != null && ds.Dbms != 0) return (int)ds.Dbms;
            }
            catch { }
            return 0;
        }

        private IDictionary<string, JArray> CollectExistingIndexes(JArray queries)
        {
            var map = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            if (_objectService == null) return map;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var q in queries)
            {
                string baseTable = (string)q["baseTable"];
                if (string.IsNullOrEmpty(baseTable) || !seen.Add(baseTable)) continue;
                try
                {
                    var tbl = _objectService.FindObject(baseTable) as Artech.Genexus.Common.Objects.Table;
                    if (tbl == null) { map[baseTable] = new JArray(); continue; }
                    var arr = new JArray();
                    dynamic dIndexesPart = ((dynamic)tbl).TableIndexes;
                    if (dIndexesPart != null && dIndexesPart.Indexes != null)
                    {
                        foreach (dynamic idxObj in dIndexesPart.Indexes)
                        {
                            dynamic idx = idxObj.Index; if (idx == null) continue;
                            var cols = new JArray();
                            if (idx.IndexStructure != null && idx.IndexStructure.Members != null)
                            {
                                foreach (dynamic m in idx.IndexStructure.Members)
                                {
                                    string n = m.Attribute != null ? (string)m.Attribute.Name : (string)m.Name;
                                    if (!string.IsNullOrEmpty(n)) cols.Add(n);
                                }
                            }
                            arr.Add(new JObject { ["name"] = (string)idx.Name, ["columns"] = cols });
                        }
                    }
                    map[baseTable] = arr;
                }
                catch
                {
                    map[baseTable] = new JArray();
                }
            }
            return map;
        }
    }
}
