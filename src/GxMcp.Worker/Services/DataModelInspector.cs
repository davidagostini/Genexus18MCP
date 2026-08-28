using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class DataModelAttribute
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsKey { get; set; }
        public bool IsFormula { get; set; }
        public string FormulaExpression { get; set; }
        public string Type { get; set; }
    }

    public class DataModelLevel
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<DataModelAttribute> Attributes { get; } = new List<DataModelAttribute>();
    }

    public class DataModelReport
    {
        public string TransactionName { get; set; }
        public string Description { get; set; }
        public List<DataModelLevel> Levels { get; } = new List<DataModelLevel>();
        public List<string> ReferencedTables { get; } = new List<string>();

        public JObject ToJson()
        {
            var obj = new JObject
            {
                ["transaction"] = TransactionName,
                ["description"] = Description
            };

            var levelsArr = new JArray();
            foreach (var lvl in Levels)
            {
                var lvlObj = new JObject
                {
                    ["name"] = lvl.Name,
                    ["description"] = lvl.Description
                };

                var attrsArr = new JArray();
                foreach (var a in lvl.Attributes)
                {
                    var aObj = new JObject
                    {
                        ["name"] = a.Name,
                        ["isKey"] = a.IsKey
                    };
                    if (!string.IsNullOrEmpty(a.Description)) aObj["description"] = a.Description;
                    if (a.IsFormula) aObj["isFormula"] = true;
                    if (!string.IsNullOrEmpty(a.FormulaExpression)) aObj["formula"] = a.FormulaExpression;
                    if (!string.IsNullOrEmpty(a.Type)) aObj["type"] = a.Type;
                    attrsArr.Add(aObj);
                }
                lvlObj["attributes"] = attrsArr;
                levelsArr.Add(lvlObj);
            }
            obj["levels"] = levelsArr;

            var tablesArr = new JArray();
            foreach (var t in ReferencedTables)
            {
                if (!string.IsNullOrEmpty(t)) tablesArr.Add(t);
            }
            obj["referencedTables"] = tablesArr;

            return obj;
        }
    }

    /// <summary>
    /// Authoritative inspector for GeneXus Transaction and Table data models,
    /// attribute structures, formulas, and relationship graphs.
    /// </summary>
    public class DataModelInspector
    {
        private readonly ObjectService _objectService;

        public DataModelInspector(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public DataModelReport InspectTransaction(string transactionName)
        {
            if (string.IsNullOrWhiteSpace(transactionName) || _objectService == null) return null;

            var trn = _objectService.FindObject(transactionName) as Transaction;
            if (trn == null) return null;

            var report = new DataModelReport
            {
                TransactionName = trn.Name,
                Description = trn.Description
            };

            try
            {
                var structure = trn.Structure;
                if (structure != null && structure.Root != null)
                {
                    TraverseLevel(structure.Root, report);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"DataModelInspector failed for {transactionName}: {ex.Message}");
            }

            return report;
        }

        private void TraverseLevel(TransactionLevel level, DataModelReport report)
        {
            if (level == null) return;

            var lvl = new DataModelLevel
            {
                Name = level.Name,
                Description = level.Description
            };

            foreach (var item in level.Attributes.OfType<TransactionAttribute>())
            {
                var globalAttr = item.Attribute;
                var attr = new DataModelAttribute
                {
                    Name = item.Name,
                    Description = globalAttr?.Description ?? item.Name,
                    IsKey = item.IsKey
                };
                if (globalAttr != null)
                {
                    attr.IsFormula = globalAttr.Formula != null;
                    attr.FormulaExpression = globalAttr.Formula?.ToString();
                    attr.Type = globalAttr.Type.ToString();
                }
                lvl.Attributes.Add(attr);
            }

            report.Levels.Add(lvl);

            foreach (TransactionLevel child in level.Levels)
            {
                TraverseLevel(child, report);
            }
        }
    }
}
