using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class KbScale40kBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public KbScale40kBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Benchmark_40k_ListObjects_Sorting()
        {
            int count = 40000;
            var candidateTypes = new[] { "Procedure", "Transaction", "WebPanel", "Attribute", "Table", "Folder", "Module", "DataProvider", "SDT" };
            var baseList = new List<SearchIndex.IndexEntry>(count);
            var rng = new Random(42);

            for (int i = 0; i < count; i++)
            {
                baseList.Add(new SearchIndex.IndexEntry
                {
                    Guid = Guid.NewGuid().ToString(),
                    Name = "Object_" + rng.Next(1, 100000),
                    Type = candidateTypes[rng.Next(candidateTypes.Length)],
                    LastUpdate = DateTime.UtcNow.AddMinutes(-rng.Next(100000))
                });
            }

            int iterations = 10;

            // Warmup
            var warmup1 = baseList.OrderBy(e => ListService.GetTypeSortBucket(e.Type)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Type, StringComparer.OrdinalIgnoreCase).ToList();
            var warmup2 = new List<SearchIndex.IndexEntry>(baseList);
            warmup2.Sort(DefaultIndexEntryComparer.Instance);
            Assert.Equal(warmup1.Count, warmup2.Count);
            Assert.Equal(warmup1[0].Name, warmup2[0].Name);

            // ANTES: LINQ OrderBy.ThenBy.ThenBy.ToList()
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int it = 0; it < iterations; it++)
            {
                var sorted = baseList
                    .OrderBy(e => ListService.GetTypeSortBucket(e.Type))
                    .ThenBy(e => e.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            sw.Stop();
            int gen0Before = GC.CollectionCount(0) - gen0Start;
            double msBefore = sw.Elapsed.TotalMilliseconds / iterations;

            // DEPOIS: In-place IntroSort with DefaultIndexEntryComparer
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int it = 0; it < iterations; it++)
            {
                var copy = new List<SearchIndex.IndexEntry>(baseList);
                copy.Sort(DefaultIndexEntryComparer.Instance);
            }
            sw.Stop();
            int gen0After = GC.CollectionCount(0) - gen0Start;
            double msAfter = sw.Elapsed.TotalMilliseconds / iterations;

            // TOP-50 Paging benchmark:
            // When limit=50 and offset=0 (the default 99% of the time in MCP calls)
            int k = 50;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int it = 0; it < iterations; it++)
            {
                // Top-K selection using PriorityQueue / bounded heap
                var topK = TopKHelper.SelectTopK(baseList, k, DefaultIndexEntryComparer.Instance, out _);
            }
            sw.Stop();
            int gen0TopK = GC.CollectionCount(0) - gen0Start;
            double msTopK = sw.Elapsed.TotalMilliseconds / iterations;

            string report = $@"
=== LIST_OBJECTS_40K_SORT_BENCHMARK ===
Sorting 40,000 KB objects (x 10 iterations):
  ANTES (Full LINQ OrderBy.ThenBy.ThenBy.ToList): {msBefore:F2} ms/sort, Gen0 Collections: {gen0Before}
  DEPOIS (Top-50 Bounded Heap for limit=50):      {msTopK:F2} ms/page, Gen0 Collections: {gen0TopK}
=======================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }

        [Fact]
        public void Benchmark_40k_Search_ByNameLookup()
        {
            int count = 40000;
            var candidateTypes = new[] { "Procedure", "Transaction", "WebPanel", "Attribute", "Table" };
            var objects = new ConcurrentDictionary<string, SearchIndex.IndexEntry>(StringComparer.OrdinalIgnoreCase);
            var byNameIndex = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < count; i++)
            {
                string name = "Obj_" + (i % 15000);
                string type = candidateTypes[i % candidateTypes.Length];
                string key = type + ":" + name;
                var entry = new SearchIndex.IndexEntry
                {
                    Guid = Guid.NewGuid().ToString(),
                    Name = name,
                    Type = type,
                    StorageKey = key
                };
                objects[key] = entry;

                var set = byNameIndex.GetOrAdd(name, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (set) { set.Add(key); }
            }

            string[] searchTargets = new[] { "Obj_42", "Obj_100", "Obj_999", "Obj_5432", "Obj_12345", "Obj_NonExistent" };
            int iterations = 1000;

            // Warmup
            foreach (var target in searchTargets)
            {
                _ = objects.Values.Where(e => string.Equals(e.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byNameIndex.TryGetValue(target, out var keys))
                {
                    _ = keys.Select(k => objects[k]).ToList();
                }
            }

            // ANTES: Full linear scan across 40k objects
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int it = 0; it < iterations; it++)
            {
                string target = searchTargets[it % searchTargets.Length];
                var results = objects.Values
                    .Where(e => string.Equals(e.Name, target, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            sw.Stop();
            int gen0Before = GC.CollectionCount(0) - gen0Start;
            double msBefore = sw.Elapsed.TotalMilliseconds / iterations;

            // DEPOIS: ByNameIndex O(1) lookup
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int it = 0; it < iterations; it++)
            {
                string target = searchTargets[it % searchTargets.Length];
                List<SearchIndex.IndexEntry> results = null;
                if (byNameIndex.TryGetValue(target, out var keys))
                {
                    results = new List<SearchIndex.IndexEntry>(keys.Count);
                    foreach (var k in keys)
                    {
                        if (objects.TryGetValue(k, out var e) && e != null)
                            results.Add(e);
                    }
                }
                results = results ?? new List<SearchIndex.IndexEntry>();
            }
            sw.Stop();
            int gen0After = GC.CollectionCount(0) - gen0Start;
            double msAfter = sw.Elapsed.TotalMilliseconds / iterations;

            string report = $@"
=== SEARCH_40K_NAME_LOOKUP_BENCHMARK ===
Exact / name:X search across 40,000 objects in memory (x 1,000 searches):
  ANTES (Full scan Objects.Values.Where): {msBefore:F3} ms/search, Gen0 Collections: {gen0Before}
  DEPOIS (ByNameIndex O(1) Multimap):     {msAfter:F5} ms/search, Gen0 Collections: {gen0After}
========================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }

        [Fact]
        public void Benchmark_40k_Validation_And_ResolveEntry()
        {
            int count = 40000;
            var candidateTypes = new[] { "Procedure", "Transaction", "WebPanel", "Attribute", "Table", "DataProvider" };
            var objects = new ConcurrentDictionary<string, SearchIndex.IndexEntry>(StringComparer.OrdinalIgnoreCase);
            var byNameIndex = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var typeIndex = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < count; i++)
            {
                string name = "Obj_" + i;
                string type = candidateTypes[i % candidateTypes.Length];
                string key = type + ":" + name;
                var entry = new SearchIndex.IndexEntry
                {
                    Guid = Guid.NewGuid().ToString(),
                    Name = name,
                    Type = type,
                    StorageKey = key,
                    Path = "Root Module/" + name
                };
                objects[key] = entry;

                var nameSet = byNameIndex.GetOrAdd(name, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (nameSet) { nameSet.Add(key); }

                var typeSet = typeIndex.GetOrAdd(type, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (typeSet) { typeSet.Add(key); }
            }

            var index = new SearchIndex
            {
                Objects = objects,
                ByNameIndex = byNameIndex,
                TypeIndex = typeIndex
            };

            // Test 1: IsKnownObject validation (references lookup during edit impact analysis)
            var references = new[] { "Obj_500", "Obj_20000", "Root Module.Obj_39999", "MissingRef_1", "MissingRef_2" };
            int valIterations = 500;

            // ANTES: linear scan over 40k objects
            int foundBefore = 0;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int it = 0; it < valIterations; it++)
            {
                string refName = references[it % references.Length];
                bool found = false;
                foreach (var entry in index.Objects.Values)
                {
                    if (entry == null) continue;
                    if (string.Equals(entry.Name, refName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(entry.Type + ":" + entry.Name, refName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                    int dot = refName.LastIndexOf('.');
                    if (dot >= 0 && string.Equals(entry.Name, refName.Substring(dot + 1), StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (found) foundBefore++;
            }
            sw.Stop();
            int gen0Before = GC.CollectionCount(0) - gen0Start;
            double msBeforeVal = sw.Elapsed.TotalMilliseconds / valIterations;

            // DEPOIS: O(1) ByNameIndex lookup
            int foundAfter = 0;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int it = 0; it < valIterations; it++)
            {
                string refName = references[it % references.Length];
                bool found = false;
                if (index.Objects.ContainsKey(refName) || index.ByNameIndex.ContainsKey(refName))
                {
                    found = true;
                }
                else
                {
                    int dot = refName.LastIndexOf('.');
                    if (dot >= 0 && dot < refName.Length - 1 && index.ByNameIndex.ContainsKey(refName.Substring(dot + 1)))
                    {
                        found = true;
                    }
                }
                if (found) foundAfter++;
            }
            sw.Stop();
            Assert.Equal(foundBefore, foundAfter);
            int gen0After = GC.CollectionCount(0) - gen0Start;
            double msAfterVal = sw.Elapsed.TotalMilliseconds / valIterations;

            // Test 2: TypeIndex candidate gathering (DbOptimize / PatternApply / ValidateConditions)
            int typeIterations = 100;

            // ANTES: linear scan over 40k objects filtering transactions
            sw.Restart();
            for (int it = 0; it < typeIterations; it++)
            {
                var txs = new List<string>();
                foreach (var entry in index.Objects.Values)
                {
                    if (entry.Type != null && entry.Type.Equals("Transaction", StringComparison.OrdinalIgnoreCase))
                        txs.Add(entry.Name);
                }
            }
            sw.Stop();
            double msBeforeType = sw.Elapsed.TotalMilliseconds / typeIterations;

            // DEPOIS: TypeIndex O(1) bucket retrieval
            sw.Restart();
            for (int it = 0; it < typeIterations; it++)
            {
                var txs = new List<string>();
                if (index.TypeIndex.TryGetValue("Transaction", out var keys) && keys != null)
                {
                    foreach (var k in keys)
                    {
                        if (index.Objects.TryGetValue(k, out var entry) && entry?.Name != null)
                            txs.Add(entry.Name);
                    }
                }
            }
            sw.Stop();
            double msAfterType = sw.Elapsed.TotalMilliseconds / typeIterations;

            string report = $@"
=== VALIDATION_AND_RESOLUTION_40K_BENCHMARK ===
1. Symbol Validation / IsKnownObject across 40k KB (x {valIterations} iterations):
   ANTES (Linear scan of Objects.Values): {msBeforeVal:F3} ms/check, Gen0: {gen0Before}
   DEPOIS (ByNameIndex O(1) check):       {msAfterVal:F5} ms/check, Gen0: {gen0After}
   Speedup: {msBeforeVal / Math.Max(0.0001, msAfterVal):F0}x mais rápido

2. Type Gathering / Candidates across 40k KB (x {typeIterations} iterations):
   ANTES (Linear scan of Objects.Values): {msBeforeType:F3} ms/filter
   DEPOIS (TypeIndex O(1) bucket):        {msAfterType:F3} ms/filter
   Speedup: {msBeforeType / Math.Max(0.0001, msAfterType):F0}x mais rápido
===============================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }

        [Fact]
        public void Benchmark_40k_HealthReport_And_PropertyMatcher()
        {
            // Benchmark 1: HealthReport across 40k objects
            int count = 40000;
            var objects = new ConcurrentDictionary<string, SearchIndex.IndexEntry>(StringComparer.OrdinalIgnoreCase);
            var rng = new Random(42);
            var types = new[] { "Procedure", "Transaction", "WebPanel", "Attribute", "Table" };

            for (int i = 0; i < count; i++)
            {
                string name = "Obj_" + i;
                string type = types[i % types.Length];
                objects[type + ":" + name] = new SearchIndex.IndexEntry
                {
                    Name = name,
                    Type = type,
                    Complexity = rng.Next(0, 100),
                    Calls = new List<string> { "DepA", "DepB" },
                    CalledBy = i % 10 == 0 ? null : new List<string> { "Caller1" }
                };
            }

            int healthIterations = 20;

            // ANTES: 6 LINQ passes + 2 OrderByDescending full sorts
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int it = 0; it < healthIterations; it++)
            {
                var entryPointTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Transaction", "WebPanel", "DataSelector", "Menu" };
                var topHotspots = objects.Values
                    .Where(o => o.Complexity > 0)
                    .OrderByDescending(o => o.Complexity)
                    .Take(10)
                    .ToList();
                var candidates = objects.Values
                    .Where(o => (o.CalledBy == null || o.CalledBy.Count == 0) && !entryPointTypes.Contains(o.Type ?? ""))
                    .OrderByDescending(o => o.Complexity)
                    .Take(20)
                    .ToList();
                double avgComplexity = objects.Values.Average(o => o.Complexity);
                int maxComplexity = objects.Values.Max(o => o.Complexity);
                int totalCalls = objects.Values.Sum(o => o.Calls.Count);
                int orphaned = objects.Values.Count(o => (o.CalledBy == null || o.CalledBy.Count == 0));
            }
            sw.Stop();
            int gen0Before = GC.CollectionCount(0) - gen0Start;
            double msBeforeHealth = sw.Elapsed.TotalMilliseconds / healthIterations;

            // DEPOIS: Single-pass accumulation with bounded Top-K
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int it = 0; it < healthIterations; it++)
            {
                var entryPointTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Transaction", "WebPanel", "DataSelector", "Menu" };
                var topHotspotsList = new List<SearchIndex.IndexEntry>(10);
                var topDeadCodeList = new List<SearchIndex.IndexEntry>(20);

                long totalComplexity = 0;
                int maxComplexity = 0;
                int totalCalls = 0;
                int orphanedCount = 0;
                int totalCount = 0;

                foreach (var o in objects.Values)
                {
                    if (o == null) continue;
                    totalCount++;
                    int c = o.Complexity;
                    totalComplexity += c;
                    if (c > maxComplexity) maxComplexity = c;
                    if (o.Calls != null) totalCalls += o.Calls.Count;
                    bool isOrphaned = (o.CalledBy == null || o.CalledBy.Count == 0);
                    if (isOrphaned) orphanedCount++;

                    if (c > 0)
                    {
                        PushTopK(topHotspotsList, o, 10);
                    }
                    if (isOrphaned && !entryPointTypes.Contains(o.Type ?? ""))
                    {
                        PushTopK(topDeadCodeList, o, 20);
                    }
                }

                topHotspotsList.Sort((a, b) => b.Complexity.CompareTo(a.Complexity));
                topDeadCodeList.Sort((a, b) => b.Complexity.CompareTo(a.Complexity));
                double avgComplexity = totalCount > 0 ? (double)totalComplexity / totalCount : 0.0;
            }
            sw.Stop();
            int gen0After = GC.CollectionCount(0) - gen0Start;
            double msAfterHealth = sw.Elapsed.TotalMilliseconds / healthIterations;

            // Benchmark 2: PropertyMatcher per-property regex vs precompiled matcher
            int propCount = 300;
            var propNames = Enumerable.Range(0, propCount).Select(i => "Property_Name_" + i).ToList();
            int propIterations = 100;
            string pattern = "*Name_15*";

            // ANTES: Regex.IsMatch inside MatchesWildcardOrQuery per property
            sw.Restart();
            int matchesBefore = 0;
            for (int it = 0; it < propIterations; it++)
            {
                foreach (var p in propNames)
                {
                    string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    if (System.Text.RegularExpressions.Regex.IsMatch(p, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        matchesBefore++;
                }
            }
            sw.Stop();
            double msBeforeProp = sw.Elapsed.TotalMilliseconds / propIterations;

            // DEPOIS: Precompiled matcher once outside the loop
            sw.Restart();
            int matchesAfter = 0;
            for (int it = 0; it < propIterations; it++)
            {
                var matcher = Services.PropertyService.BuildPropertyMatcher(pattern);
                foreach (var p in propNames)
                {
                    if (matcher(p))
                        matchesAfter++;
                }
            }
            sw.Stop();
            double msAfterProp = sw.Elapsed.TotalMilliseconds / propIterations;
            Assert.Equal(matchesBefore, matchesAfter);

            string report = $@"
=== HEALTH_AND_PROPERTY_BENCHMARK ===
1. HealthReport across 40,000 objects (x {healthIterations} iterations):
   ANTES (6 LINQ passes + 2 40k Sorts): {msBeforeHealth:F2} ms/report, Gen0: {gen0Before}
   DEPOIS (Single-pass + Bounded Top-K): {msAfterHealth:F2} ms/report, Gen0: {gen0After}
   Speedup: {msBeforeHealth / Math.Max(0.001, msAfterHealth):F1}x mais rápido ({100 * (msBeforeHealth - msAfterHealth) / msBeforeHealth:F1}% menos tempo)

2. Property Wildcard Matching across {propCount} properties (x {propIterations} iterations):
   ANTES (Dynamically compiled Regex per prop): {msBeforeProp:F3} ms/inspection
   DEPOIS (Precompiled Matcher once per call):  {msAfterProp:F3} ms/inspection
   Speedup: {msBeforeProp / Math.Max(0.0001, msAfterProp):F1}x mais rápido ({100 * (msBeforeProp - msAfterProp) / msBeforeProp:F1}% menos tempo)
=====================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }

        private static void PushTopK(List<SearchIndex.IndexEntry> list, SearchIndex.IndexEntry item, int k)
        {
            if (list.Count < k)
            {
                list.Add(item);
                if (list.Count == k)
                {
                    list.Sort((a, b) => a.Complexity.CompareTo(b.Complexity));
                }
            }
            else if (item.Complexity > list[0].Complexity)
            {
                list[0] = item;
                list.Sort((a, b) => a.Complexity.CompareTo(b.Complexity));
            }
        }
    }
}
