using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    public class PatternService
    {
        private readonly IndexCacheService _indexCache;
        private readonly ObjectService _objectService;

        public PatternService(IndexCacheService indexCache, ObjectService objectService)
        {
            _indexCache = indexCache;
            _objectService = objectService;
        }

        public string GetSample(string type)
        {
            try
            {
                var index = _indexCache.GetIndex();
                if (index == null) return McpResponse.Err(
                    code: "SearchIndexMissing",
                    message: "Search Index not found.",
                    hint: "Run the KB indexing flow before requesting pattern samples.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_lifecycle",
                        args: new JObject { ["action"] = "index" },
                        why: "Builds the on-disk SearchIndex required for pattern samples.")),
                    retryAfterMs: 10000,
                    target: type);

                IEnumerable<SearchIndex.IndexEntry> candidatesSource = null;
                if (index.TypeIndex != null && index.TypeIndex.Count > 0)
                {
                    var matchingTypes = index.TypeIndex.Keys
                        .Where(k => !string.IsNullOrEmpty(k) && IsTypeMatch(k, type))
                        .ToList();
                    if (matchingTypes.Count > 0)
                    {
                        candidatesSource = index.FindByTypes(matchingTypes);
                    }
                }
                if (candidatesSource == null && index.Objects != null)
                {
                    candidatesSource = index.Objects.Values.Where(o => o != null && IsTypeMatch(o.Type, type));
                }
                if (candidatesSource == null)
                {
                    candidatesSource = Enumerable.Empty<SearchIndex.IndexEntry>();
                }

                var candidates = candidatesSource
                    .Where(o => o != null && o.Complexity > 5 && o.CalledBy != null && o.CalledBy.Count > 2)
                    .OrderBy(o => o.Complexity)
                    .Take(5)
                    .ToList();

                if (candidates.Count == 0)
                {
                    candidates = candidatesSource.Where(o => o != null).Take(1).ToList();
                }

                if (candidates.Count == 0) return McpResponse.Err(
                    code: "PatternSampleNotFound",
                    message: "Pattern sample not found.",
                    hint: "No indexed objects matched the requested type. Try a different type name or rebuild the index.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_lifecycle",
                        args: new JObject { ["action"] = "index", ["force"] = true },
                        why: "Rebuilds the index so newly added objects of the requested type are discoverable.")),
                    target: type);

                var best = candidates.First();
                
                string sourceJson = _objectService.ReadObjectSource(best.Name, "Source");
                var json = JObject.Parse(sourceJson);
                string source = json["source"] != null ? json["source"].ToString() : "// No source available";
                
                var result = new JObject();
                result["sampleName"] = best.Name;
                result["type"] = best.Type;
                result["complexity"] = best.Complexity;
                result["source"] = source ?? "// No source available";

                return McpResponse.Ok(target: type, code: "PatternSampleFound", result: result);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "PatternSampleFailed",
                    message: ex.Message,
                    hint: "Inspect the worker log; the index or object source may be corrupt.",
                    nextSteps: new JArray(McpResponse.NextStep(
                        tool: "genexus_lifecycle",
                        args: new JObject { ["action"] = "index", ["force"] = true },
                        why: "Rebuilds the index from scratch if the cached data is corrupt.")),
                    target: type);
            }
        }

        private bool IsTypeMatch(string actual, string expected)
        {
            if (string.IsNullOrEmpty(actual)) return false;
            return actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
