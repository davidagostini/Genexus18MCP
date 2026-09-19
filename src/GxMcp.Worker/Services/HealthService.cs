using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class HealthService
    {
        private readonly IndexCacheService _indexCacheService;
        private readonly string _legacyIndexPath;

        public HealthService()
        {
            // Keep the parameterless constructor's old direct-use behavior for callers that
            // instantiate the service outside the Worker composition root. The production
            // dispatcher uses the injected constructor below, which has the KB-scoped cache.
            _legacyIndexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "search_index.json");
        }

        public HealthService(IndexCacheService indexCacheService)
        {
            _indexCacheService = indexCacheService ?? throw new ArgumentNullException(nameof(indexCacheService));
        }

        public string Ping()
        {
            return McpResponse.Ok(
                code: "Ready",
                result: new JObject { ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
        }

        public string GetHealthReport()
        {
            try
            {
                SearchIndex index;
                if (_legacyIndexPath != null)
                {
                    if (!File.Exists(_legacyIndexPath))
                    {
                        return McpResponse.Err(
                            code: "SearchIndexMissing",
                            message: "Search Index not found.",
                            hint: "Run the KB indexing flow before requesting the health report.",
                            nextSteps: new JArray(
                                McpResponse.NextStep(
                                    tool: "genexus_lifecycle",
                                    args: new JObject { ["action"] = "index" },
                                    why: "Builds the on-disk SearchIndex this report reads.")),
                            retryAfterMs: 10000);
                    }

                    index = SearchIndex.FromJson(File.ReadAllText(_legacyIndexPath));
                    if (index == null || index.Objects == null || index.Objects.Count == 0)
                    {
                        return McpResponse.Err(
                            code: "SearchIndexEmpty",
                            message: "Search Index is empty.",
                            hint: "The health report needs an indexed KB; rebuild the index after opening a populated KB.",
                            nextSteps: new JArray(
                                McpResponse.NextStep(
                                    tool: "genexus_lifecycle",
                                    args: new JObject { ["action"] = "index", ["force"] = true },
                                    why: "Forces a full rebuild of the SearchIndex on the active KB.")),
                            retryAfterMs: 10000);
                    }
                }
                else
                {
                    index = _indexCacheService.GetIndex();
                    if (index == null || index.Objects == null || index.Objects.Count == 0)
                    {
                        bool missing = _indexCacheService.IsIndexMissing;
                        if (missing)
                        {
                            return McpResponse.Err(
                                code: "SearchIndexMissing",
                                message: "Search Index not found.",
                                hint: "Run the KB indexing flow before requesting the health report.",
                                nextSteps: new JArray(
                                    McpResponse.NextStep(
                                        tool: "genexus_lifecycle",
                                        args: new JObject { ["action"] = "index" },
                                        why: "Builds the on-disk SearchIndex this report reads.")),
                                retryAfterMs: 10000);
                        }

                        return McpResponse.Err(
                            code: "SearchIndexEmpty",
                            message: "Search Index is empty.",
                            hint: "The health report needs an indexed KB; rebuild the index after opening a populated KB.",
                            nextSteps: new JArray(
                                McpResponse.NextStep(
                                    tool: "genexus_lifecycle",
                                    args: new JObject { ["action"] = "index", ["force"] = true },
                                    why: "Forces a full rebuild of the SearchIndex on the active KB.")),
                            retryAfterMs: 10000);
                    }
                }

                var report = new JObject();
                report["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                report["totalObjects"] = index.Objects.Count;

                // 1. Single pass over index objects for Hotspots, Dead Code, and Summary Stats
                var entryPointTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Transaction", "WebPanel", "DataSelector", "Menu" };
                var topHotspots = new TopKHelper.BoundedHeap<SearchIndex.IndexEntry>(10, ComplexityDescendingComparer.Instance);
                var topDeadCode = new TopKHelper.BoundedHeap<SearchIndex.IndexEntry>(20, ComplexityDescendingComparer.Instance);

                long totalComplexity = 0;
                int maxComplexity = 0;
                int totalCalls = 0;
                int orphanedCount = 0;
                int totalCount = 0;

                foreach (var o in index.Objects.Values)
                {
                    if (o == null) continue;
                    totalCount++;
                    int c = o.Complexity;
                    totalComplexity += c;
                    if (c > maxComplexity) maxComplexity = c;
                    if (o.Calls != null) totalCalls += o.Calls.Count;
                    bool isOrphaned = (o.CalledBy == null || o.CalledBy.Count == 0);
                    if (isOrphaned) orphanedCount++;

                    // 1. Complexity Hotspots
                    if (c > 0)
                    {
                        topHotspots.Push(o);
                    }

                    // 2. Dead Code Detection
                    if (isOrphaned && !entryPointTypes.Contains(o.Type ?? "") && !IsMainObject(o))
                    {
                        topDeadCode.Push(o);
                    }
                }

                var topHotspotsList = topHotspots.ToSortedList();
                var topDeadCodeList = topDeadCode.ToSortedList();

                var hotspots = new JArray();
                foreach (var o in topHotspotsList)
                {
                    var item = new JObject();
                    item["name"] = o.Name;
                    item["complexity"] = o.Complexity;
                    item["type"] = o.Type;
                    hotspots.Add(item);
                }
                report["complexityHotspots"] = hotspots;

                var deadCode = new JArray();
                foreach (var o in topDeadCodeList)
                {
                    var item = new JObject();
                    item["name"] = o.Name;
                    item["type"] = o.Type;
                    item["complexity"] = o.Complexity;
                    deadCode.Add(item);
                }
                report["deadCodeCandidates"] = deadCode;

                // 3. Circular Dependencies
                var cycles = FindCircularDependencies(index);
                var cyclesArray = new JArray();
                foreach (var cycle in cycles)
                {
                    cyclesArray.Add(string.Join(" -> ", cycle));
                }
                report["circularDependencies"] = cyclesArray;

                // 4. Summary Stats
                var stats = new JObject();
                stats["avgComplexity"] = totalCount > 0 ? (double)totalComplexity / totalCount : 0.0;
                stats["maxComplexity"] = maxComplexity;
                stats["totalCalls"] = totalCalls;
                stats["orphanedObjects"] = orphanedCount;
                report["summary"] = stats;

                return McpResponse.Ok(code: "HealthReport", result: report);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "HealthReportFailed",
                    message: ex.Message,
                    hint: "Inspect the worker log; the SearchIndex JSON may be corrupt. Rebuild via genexus_lifecycle action=index force=true.",
                    nextSteps: new JArray(
                        McpResponse.NextStep(
                            tool: "genexus_lifecycle",
                            args: new JObject { ["action"] = "index", ["force"] = true },
                            why: "Rebuilds the index from scratch if the cached file is corrupt.")));
            }
        }

        private sealed class ComplexityDescendingComparer : IComparer<SearchIndex.IndexEntry>
        {
            public static readonly ComplexityDescendingComparer Instance = new ComplexityDescendingComparer();

            public int Compare(SearchIndex.IndexEntry x, SearchIndex.IndexEntry y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return 1;
                if (y == null) return -1;
                int c = y.Complexity.CompareTo(x.Complexity);
                if (c != 0) return c;
                return string.Compare(x.Name ?? string.Empty, y.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }

        private bool IsMainObject(SearchIndex.IndexEntry entry)
        {
            if (entry == null) return false;
            if (entry.Name != null && entry.Name.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (entry.Description != null && entry.Description.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (entry.Tags != null && entry.Tags.Any(t => t.Equals("Main", StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }

        private List<List<string>> FindCircularDependencies(SearchIndex index)
        {
            var cycles = new List<List<string>>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new List<string>();

            foreach (var node in index.Objects.Keys)
            {
                if (!visited.Contains(node))
                {
                    DFS(node, visited, stack, inStack, index, cycles);
                    if (cycles.Count >= 50) break;
                }
            }

            return cycles;
        }

        private void DFS(string current, HashSet<string> visited, List<string> stack, HashSet<string> inStack, SearchIndex index, List<List<string>> cycles)
        {
            if (cycles.Count >= 50) return;
            visited.Add(current);
            stack.Add(current);
            inStack.Add(current);

            if (index.Objects.TryGetValue(current, out var entry) && entry.Calls != null)
            {
                foreach (var neighbor in entry.Calls)
                {
                    if (inStack.Contains(neighbor))
                    {
                        int indexInStack = stack.FindIndex(s => s.Equals(neighbor, StringComparison.OrdinalIgnoreCase));
                        if (indexInStack >= 0)
                        {
                            // Cycle detected
                            var cycle = stack.Skip(indexInStack).ToList();
                            cycle.Add(neighbor);
                            if (cycles.Count < 50) 
                            {
                                if (!IsDuplicateCycle(cycle, cycles))
                                    cycles.Add(cycle);
                            }
                        }
                    }
                    else if (!visited.Contains(neighbor))
                    {
                        DFS(neighbor, visited, stack, inStack, index, cycles);
                        if (cycles.Count >= 50) break;
                    }
                }
            }

            inStack.Remove(current);
            stack.RemoveAt(stack.Count - 1);
        }

        private bool IsDuplicateCycle(List<string> newCycle, List<List<string>> cycles)
        {
            var newSet = new HashSet<string>(newCycle, StringComparer.OrdinalIgnoreCase);
            foreach (var c in cycles)
            {
                if (c.Count == newCycle.Count && newSet.SetEquals(c))
                    return true;
            }
            return false;
        }
    }
}
