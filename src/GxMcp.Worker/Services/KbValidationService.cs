using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class KbValidationService
    {
        private readonly IndexCacheService _indexCacheService;
        private readonly ObjectService _objectService;
        private readonly PatternAnalysisService _patternAnalysisService;

        private static readonly HashSet<string> _keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "and", "or", "not", "when", "isempty", "true", "false", "null", "nullvalue",
            "like", "in", "contains", "between", "from", "to", "if", "then", "else",
            "endif", "for", "endfor", "do", "exists", "noexists", "any", "count"
        };

        public KbValidationService(IndexCacheService indexCacheService, ObjectService objectService, PatternAnalysisService patternAnalysisService)
        {
            _indexCacheService = indexCacheService;
            _objectService = objectService;
            _patternAnalysisService = patternAnalysisService;
        }

        public string ValidateConditions(int limit = 0)
        {
            try
            {
                var index = _indexCacheService.GetIndex();
                if (index == null || index.Objects.Count == 0)
                    return McpResponse.Err(
                        code: "IndexEmpty",
                        message: "Search index is empty.",
                        hint: "Run genexus_lifecycle action=index first.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_lifecycle",
                            args: new JObject { ["action"] = "index" },
                            why: "Builds the on-disk search index required for validation.")));

                var attrNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in index.Objects.Values)
                {
                    if (string.Equals(entry.Type, "Attribute", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entry.Name))
                        attrNames.Add(entry.Name);
                }

                var candidates = index.Objects.Values
                    .Where(e => string.Equals(e.Type, "Transaction", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(e.Type, "WebPanel", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var issues = new JArray();
                int scanned = 0;
                int patternsFound = 0;

                foreach (var entry in candidates)
                {
                    if (limit > 0 && scanned >= limit) break;
                    scanned++;

                    string xml;
                    try
                    {
                        var obj = _objectService.FindObject(entry.Name, entry.Type);
                        if (obj == null) continue;
                        xml = _patternAnalysisService.ReadPatternPartXml(obj, "PatternInstance", out _, out _);
                    }
                    catch { continue; }

                    if (string.IsNullOrWhiteSpace(xml)) continue;
                    patternsFound++;

                    XDocument doc;
                    try { doc = XDocument.Parse(xml); }
                    catch { continue; }

                    foreach (var ga in doc.Descendants("gridAttribute"))
                    {
                        var conditions = ga.Attribute("conditions")?.Value;
                        if (string.IsNullOrWhiteSpace(conditions)) continue;

                        var attribAttr = ga.Attribute("attribute")?.Value ?? string.Empty;
                        var dash = attribAttr.LastIndexOf('-');
                        var controlName = dash >= 0 ? attribAttr.Substring(dash + 1) : attribAttr;

                        var missing = ExtractMissingAttributes(conditions, attrNames);
                        if (missing.Count > 0)
                        {
                            foreach (var m in missing)
                            {
                                issues.Add(new JObject
                                {
                                    ["object"] = entry.Name,
                                    ["objectType"] = entry.Type,
                                    ["control"] = controlName,
                                    ["conditions"] = conditions,
                                    ["missingAttribute"] = m,
                                    ["suggestion"] = "Attribute '" + m + "' not found in KB. Verify spelling or rename in PatternInstance."
                                });
                            }
                        }
                    }
                }

                string resultCode = issues.Count == 0 ? "ConditionsOk" : "IssuesFound";
                return McpResponse.Ok(
                    code: resultCode,
                    result: new JObject
                    {
                        ["scannedObjects"] = scanned,
                        ["patternInstancesInspected"] = patternsFound,
                        ["issuesCount"] = issues.Count,
                        ["issues"] = issues
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "ValidateConditionsFailed",
                    message: ex.Message,
                    hint: "Ensure the search index is built and the KB is open.");
            }
        }

        public string ListPatternSnapshots(string target)
        {
            try
            {
                if (string.IsNullOrEmpty(target))
                    return McpResponse.Err(
                        code: "MissingTarget",
                        message: "target is required.",
                        hint: "Provide the object name to list pattern snapshots.");
                var obj = _objectService.FindObject(target);
                if (obj == null) return McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object not found.",
                    hint: "Use type=<...> to disambiguate if multiple objects share the name.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_list_objects",
                        args: new JObject(),
                        why: "Lists objects so you can find the correct name and type.")),
                    target: target);

                var arr = new JArray();

                // Pattern-instance snapshots (WWP reapply guard) — kept for back-compat.
                var patternFiles = PatternSnapshotStore.List(obj.Guid.ToString());
                foreach (var f in patternFiles) arr.Add(new JObject
                {
                    ["path"] = f,
                    ["fileName"] = System.IO.Path.GetFileName(f),
                    ["sizeBytes"] = new System.IO.FileInfo(f).Length,
                    ["kind"] = "pattern"
                });

                // issue #43 #3 — the pre-write .bak that every WriteObject captures lives in the
                // EditSnapshotStore, not the pattern store; surface it here too so the destructive
                // edit's own backup is listable (and restorable) through the tool.
                string kbPath = null;
                try { kbPath = _objectService.GetKbService().GetKbPath(); } catch { }
                string editRoot = EditSnapshotStore.ResolveRoot(kbPath);
                foreach (var e in EditSnapshotStore.ListForGuid(editRoot, obj.Guid.ToString()))
                {
                    arr.Add(new JObject
                    {
                        ["path"] = e.Path,
                        ["fileName"] = e.FileName,
                        ["sizeBytes"] = e.Bytes,
                        ["part"] = e.Part,
                        ["timestamp"] = e.Timestamp,
                        ["kind"] = "edit"
                    });
                }

                return McpResponse.Ok(
                    target: obj.Name,
                    code: "PatternSnapshotList",
                    result: new JObject { ["count"] = arr.Count, ["snapshots"] = arr });
            }
            catch (Exception ex) { return McpResponse.Err(code: "ListPatternSnapshotsFailed", message: ex.Message, target: target); }
        }

        public string RestorePatternSnapshot(string target, string snapshotPath, WriteService writeService)
        {
            try
            {
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(snapshotPath))
                    return McpResponse.Err(
                        code: "MissingArguments",
                        message: "target and snapshotPath are required.",
                        hint: "Use the snapshots-list action to find available paths.",
                        target: target);

                var xml = PatternSnapshotStore.ReadSnapshot(snapshotPath);
                if (string.IsNullOrEmpty(xml))
                    return McpResponse.Err(
                        code: "SnapshotReadFailed",
                        message: "File missing or unreadable: " + snapshotPath,
                        hint: "List available snapshots to find a valid path.",
                        nextSteps: new JArray(McpResponse.NextStep(
                            tool: "genexus_kb_validate",
                            args: new JObject { ["action"] = "snapshots-list", ["target"] = target },
                            why: "Lists available pattern snapshots for this object.")),
                        target: target);

                return writeService.WriteObject(target, "PatternInstance", xml);
            }
            catch (Exception ex) { return McpResponse.Err(code: "RestorePatternSnapshotFailed", message: ex.Message, target: target); }
        }

        public List<BrokenRef> AnalyzeImpact(string targetName, string afterXml)
        {
            Logger.Warn("Impact analysis is not implemented; dryRun brokenRefs is advisory only.");
            return new List<BrokenRef>();
        }

        private List<string> ExtractMissingAttributes(string expression, HashSet<string> known)
        {
            var missing = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(expression, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
            {
                var token = m.Value;
                if (_keywords.Contains(token)) continue;
                if (token.Length <= 1) continue;
                if (seen.Contains(token)) continue;
                seen.Add(token);
                if (!known.Contains(token)) missing.Add(token);
            }
            return missing;
        }

    }
}
