using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services.Structure;

namespace GxMcp.Worker.Services
{
    public class StructureService
    {
        private readonly ObjectService _objectService;
        private readonly VisualStructureService _visualStructureService;
        private readonly IndexService _indexService;
        private readonly AttributeWriteService _attributeWriteService;
        private readonly DomainWriteService _domainWriteService;
        private readonly SDTService _sdtService;

        public StructureService(ObjectService objectService)
        {
            _objectService = objectService;
            _visualStructureService = new VisualStructureService(objectService);
            _indexService = new IndexService(objectService);
            _attributeWriteService = new AttributeWriteService(objectService);
            _domainWriteService = new DomainWriteService(objectService);
            _sdtService = new SDTService(objectService);
        }

        public string UpdateVisualStructure(string targetName, string payload)
        {
            try {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());

                // issue #52: SDT structure updates (root Collection flag + item name, Domain-based
                // and SDT-reference members, nested levels) go through the SDT-specific writer —
                // the Transaction path below can't express any of that.
                if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                    return _sdtService.UpdateSDTStructure(targetName, payload);

                var trn = obj as Transaction;
                if (trn == null) return Models.McpResponse.Err(
                    code: "NotATransaction",
                    message: "Object is not a Transaction or SDT.",
                    hint: "Visual structure updates support Transaction and SDT objects.",
                    target: targetName,
                    nextSteps: new Newtonsoft.Json.Linq.JArray(Models.McpResponse.NextStep(
                        tool: "genexus_analyze",
                        args: new Newtonsoft.Json.Linq.JObject { ["name"] = targetName },
                        why: "Confirms the object type before attempting a structure update.")));

                using (var sdkTrans = trn.Model.KB.BeginTransaction()) {
                    try {
                        var json = JObject.Parse(payload);
                        var children = json["children"] as JArray;
                        if (children == null) return Models.McpResponse.Err(
                            code: "InvalidStructurePayload",
                            message: "The payload must contain a 'children' array for visual structure updates.",
                            hint: "Pass a JSON object with a 'children' array describing the Transaction structure.",
                            target: targetName);
                        
                        // Chamada otimizada com Batch Save interno
                        _visualStructureService.SyncVisualStructure(trn, children);
                        
                        trn.EnsureSave();
                        sdkTrans.Commit();
                        
                        _objectService.GetKbService().GetIndexCache().UpdateEntry(trn);
                        return Models.McpResponse.Ok(target: targetName, code: "StructureUpdated");
                    } catch (Exception ex) {
                        sdkTrans.Rollback();
                        return Models.McpResponse.Err(
                            code: "StructureUpdateFailed",
                            message: ex.Message,
                            hint: "Check the payload children array for malformed items, then retry.",
                            target: targetName);
                    }
                }
            } catch (Exception ex) {
                return Models.McpResponse.Err(
                    code: "StructureUpdateFailed",
                    message: ex.Message,
                    hint: "Ensure the target Transaction exists and the payload is valid JSON.",
                    target: targetName);
            }
        }

        public string GetVisualStructure(string targetName)
        {
            try {
                Logger.Info($"[StructureService] Loading visual structure for: {targetName}");
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());
                
                Logger.Info($"[StructureService] Found object: {obj.Name} ({obj.TypeDescriptor.Name})");

                if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                {
                    return _sdtService.GetSDTStructure(targetName);
                }

                var result = new JObject { ["name"] = obj.Name, ["type"] = obj.TypeDescriptor.Name, ["description"] = obj.Description };
                if (obj is Transaction trn) {
                    Logger.Info($"[StructureService] Serializing Transaction Level: {trn.Name}");
                    result["children"] = _visualStructureService.SerializeVisualLevel(trn.Structure.Root);
                }
                else if (obj is Table tbl) {
                    Logger.Info($"[StructureService] Serializing Table Structure: {tbl.Name}");
                    result["children"] = SerializeTableStructure(tbl);
                }
                else {
                    Logger.Error($"[StructureService] Invalid object type for visual structure: {obj.TypeDescriptor.Name}");
                    return Models.McpResponse.Err(
                        code: "UnsupportedObjectType",
                        message: "Visual structure is available only for Transaction, Table, or SDT objects.",
                        hint: "Use genexus_analyze to inspect this object type.",
                        target: targetName,
                        nextSteps: new Newtonsoft.Json.Linq.JArray(Models.McpResponse.NextStep(
                            tool: "genexus_analyze",
                            args: new Newtonsoft.Json.Linq.JObject { ["name"] = targetName },
                            why: "Returns a summary of the object including its type.")));
                }

                result["_meta"] = new JObject
                {
                    ["suggested_next"] = new JObject
                    {
                        ["tool"] = "genexus_read",
                        ["args"] = new JObject { ["name"] = obj.Name, ["type"] = obj.TypeDescriptor.Name }
                    }
                };

                Logger.Info($"[StructureService] Successfully serialized structure for {obj.Name}");
                return Models.McpResponse.Ok(target: targetName, code: "StructureRead", result: result);
            } catch (Exception ex) {
                Logger.Error($"[StructureService] Error loading visual structure: {ex.Message}\n{ex.StackTrace}");
                return Models.McpResponse.Err(
                    code: "StructureReadFailed",
                    message: ex.Message,
                    hint: "Ensure the target is a Transaction, Table, or SDT.",
                    target: targetName);
            }
        }

        public string GetVisualIndexes(string targetName) => _indexService.GetVisualIndexes(targetName);

        public string CreateIndex(string targetName, string payload) => _indexService.CreateIndex(targetName, payload);

        public string DropIndex(string targetName, string payload) => _indexService.DropIndex(targetName, payload);

        public string SetAttributeProperties(string attrName, string payload) => _attributeWriteService.SetAttributeProperties(attrName, payload);

        public string SetDomainProperties(string domainName, string payload) => _domainWriteService.SetDomainProperties(domainName, payload);

        // issue #39 follow-up: set a Transaction level's Description / Image attribute — level
        // properties the structure DSL doesn't express. payload = { level?, descriptionAttribute?, imageAttribute? }.
        // level omitted → the root (first) level.
        public string SetLevelProperties(string targetName, string payload)
        {
            try
            {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());
                if (!(obj is Transaction trn)) return Models.McpResponse.Err(
                    code: "NotATransaction",
                    message: "Level properties apply only to Transactions.",
                    target: targetName);
                if (string.IsNullOrWhiteSpace(payload)) return Models.McpResponse.Err(
                    code: "InvalidPayload",
                    message: "payload is required.",
                    hint: "e.g. { \"descriptionAttribute\": \"CustomerName\" } or { \"imageAttribute\": \"CustomerPhoto\" }.",
                    target: targetName);

                var json = JObject.Parse(payload);
                string levelName = json["level"]?.ToString();

                TransactionLevel level = trn.Structure.Root;
                if (!string.IsNullOrWhiteSpace(levelName))
                {
                    level = FindLevel(trn.Structure.Root, levelName);
                    if (level == null) return Models.McpResponse.Err(
                        code: "LevelNotFound",
                        message: $"Level '{levelName}' not found in transaction '{trn.Name}'.",
                        hint: "Omit 'level' to target the root level, or pass an existing sub-level name.",
                        target: targetName);
                }

                TransactionAttribute FindLevelAttr(string an) =>
                    level.Attributes.FirstOrDefault(a => string.Equals(a.Name, an, StringComparison.OrdinalIgnoreCase));

                var applied = new JArray();
                using (var sdkTrans = trn.Model.KB.BeginTransaction())
                {
                    try
                    {
                        if (json["descriptionAttribute"] != null)
                        {
                            string an = json["descriptionAttribute"].ToString();
                            var ta = FindLevelAttr(an);
                            if (ta == null) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                                code: "AttributeNotInLevel",
                                message: $"Attribute '{an}' is not part of level '{level.Name}'.",
                                hint: "The description attribute must be one of the level's own attributes.",
                                target: targetName); }
                            level.IsDescriptionAttributeDefault = false;
                            level.DescriptionAttribute = ta;
                            applied.Add("descriptionAttribute");
                        }
                        if (json["imageAttribute"] != null)
                        {
                            string an = json["imageAttribute"].ToString();
                            var ta = FindLevelAttr(an);
                            if (ta == null) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                                code: "AttributeNotInLevel",
                                message: $"Attribute '{an}' is not part of level '{level.Name}'.",
                                target: targetName); }
                            level.IsImageAttributeDefault = false;
                            level.ImageAttribute = ta;
                            applied.Add("imageAttribute");
                        }

                        if (applied.Count == 0) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                            code: "NoPropertiesToApply",
                            message: "payload contained no recognized level properties.",
                            hint: "Recognized: descriptionAttribute, imageAttribute (optionally scoped by level).",
                            target: targetName); }

                        trn.EnsureSave();
                        sdkTrans.Commit();
                        return Models.McpResponse.Ok(
                            target: targetName,
                            code: "LevelUpdated",
                            result: new JObject { ["level"] = level.Name, ["applied"] = applied });
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        return Models.McpResponse.Err(
                            code: "LevelUpdateFailed",
                            message: ex.Message,
                            hint: "Check the worker log for the SDK stack trace.",
                            target: targetName,
                            extra: new JObject { ["stackTrace"] = ex.StackTrace });
                    }
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "LevelUpdateFailed",
                    message: ex.Message,
                    hint: "Ensure the transaction exists and payload is valid JSON.",
                    target: targetName);
            }
        }

        private static TransactionLevel FindLevel(TransactionLevel root, string name)
        {
            if (root == null) return null;
            if (string.Equals(root.Name, name, StringComparison.OrdinalIgnoreCase)) return root;
            foreach (var child in root.Levels)
            {
                var found = FindLevel(child, name);
                if (found != null) return found;
            }
            return null;
        }

        public string GetLogicStructure(string targetName)
        {
            try {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());

                var result = new JObject { ["name"] = obj.Name, ["type"] = obj.TypeDescriptor.Name };
                var subs = new JArray();
                var events = new JArray();

                // 1. Get Source Part (Subs usually here)
                try {
                    var sourcePart = obj.Parts.Get<global::Artech.Genexus.Common.Parts.SourcePart>();
                    if (sourcePart != null) ExtractLogicItems(sourcePart.Source, subs, events);
                } catch { }

                // 2. Get Events Part (Events usually here)
                try {
                    var eventsPart = obj.Parts.Get<global::Artech.Genexus.Common.Parts.EventsPart>();
                    if (eventsPart != null) ExtractLogicItems(eventsPart.Source, subs, events);
                } catch { }

                result["subs"] = subs;
                result["events"] = events;
                result["_meta"] = new JObject
                {
                    ["suggested_next"] = new JObject
                    {
                        ["tool"] = "genexus_read",
                        ["args"] = new JObject { ["name"] = obj.Name, ["type"] = obj.TypeDescriptor.Name }
                    }
                };
                return Models.McpResponse.Ok(target: targetName, code: "LogicStructureRead", result: result);
            }
            catch (Exception ex) {
                return Models.McpResponse.Err(
                    code: "LogicStructureReadFailed",
                    message: ex.Message,
                    hint: "Ensure the target object exists and has a Source or Events part.",
                    target: targetName);
            }
        }

        private void ExtractLogicItems(string source, JArray subs, JArray events)
        {
            if (string.IsNullOrEmpty(source)) return;

            // Sub Extraction
            var subMatches = System.Text.RegularExpressions.Regex.Matches(source, @"(?i)\bsub\s+['""]?([\w\.]+)['""]?");
            foreach (System.Text.RegularExpressions.Match match in subMatches)
            {
                string name = match.Groups[1].Value;
                if (!subs.Any(s => s.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)))
                    subs.Add(name);
            }

            // Event Extraction
            var eventMatches = System.Text.RegularExpressions.Regex.Matches(source, @"(?i)\bevent\s+['""]?([\w\.]+)['""]?");
            foreach (System.Text.RegularExpressions.Match match in eventMatches)
            {
                string name = match.Groups[1].Value;
                if (!events.Any(e => e.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)))
                    events.Add(name);
            }
        }

        private JArray SerializeTableStructure(Table tbl)
        {
            var children = new JArray();
            dynamic dStructure = ((dynamic)tbl).TableStructure;
            if (dStructure != null && dStructure.Attributes != null) {
                foreach (dynamic attr in dStructure.Attributes) children.Add(VisualStructureMapper.MapAttribute(attr));
            }
            return children;
        }
    }
}
