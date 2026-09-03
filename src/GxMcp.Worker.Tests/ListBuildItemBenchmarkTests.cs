using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class ListBuildItemBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public ListBuildItemBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static bool IsLegacyPerfProfile_Uncached()
        {
            string perfProfile = Environment.GetEnvironmentVariable("MCP_PERF_PROFILE");
            return !string.IsNullOrWhiteSpace(perfProfile) &&
                   string.Equals(perfProfile, "legacy", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly bool _cachedLegacy = IsLegacyPerfProfile_Uncached();

        private static JObject BuildItem_Before(string name, string type, string description, string parent, string module, string path, string parentPath, string parentFolderPath, bool verbose, DateTime lastUpdate)
        {
            bool legacyMode = IsLegacyPerfProfile_Uncached();
            var item = new JObject();
            item["name"] = name;
            item["type"] = type;
            if (legacyMode || verbose)
            {
                item["description"] = description;
                item["parent"] = parent;
                item["module"] = module;
                item["path"] = path;
                item["parentPath"] = parentPath;
            }
            else
            {
                item["path"] = path;
                item["parent"] = parent;
                if (!string.IsNullOrEmpty(parentPath))
                {
                    item["parentPath"] = parentPath;
                }
            }
            return item;
        }

        private static JObject BuildItem_After(string name, string type, string description, string parent, string module, string path, string parentPath, string parentFolderPath, bool verbose, DateTime lastUpdate, bool legacyMode)
        {
            var item = new JObject();
            item["name"] = name;
            item["type"] = type;
            if (legacyMode || verbose)
            {
                item["description"] = description;
                item["parent"] = parent;
                item["module"] = module;
                item["path"] = path;
                item["parentPath"] = parentPath;
            }
            else
            {
                item["path"] = path;
                item["parent"] = parent;
                if (!string.IsNullOrEmpty(parentPath))
                {
                    item["parentPath"] = parentPath;
                }
            }
            return item;
        }

        [Fact]
        public void Benchmark_BuildItem_Throughput()
        {
            int count = 200;
            int iterations = 2000;

            // Warmup
            for (int i = 0; i < 100; i++)
            {
                _ = BuildItem_Before("Test", "Procedure", "Desc", "Parent", "Module", "Path", "ParentPath", "Root", false, DateTime.UtcNow);
                _ = BuildItem_After("Test", "Procedure", "Desc", "Parent", "Module", "Path", "ParentPath", "Root", false, DateTime.UtcNow, _cachedLegacy);
            }

            // ANTES: GetEnvironmentVariable per item
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int it = 0; it < iterations; it++)
            {
                for (int i = 0; i < count; i++)
                {
                    _ = BuildItem_Before("Test", "Procedure", "Desc", "Parent", "Module", "Path", "ParentPath", "Root", false, DateTime.UtcNow);
                }
            }
            sw.Stop();
            int gen0Before = GC.CollectionCount(0) - gen0Start;
            double msBefore = sw.Elapsed.TotalMilliseconds / iterations;

            // DEPOIS: legacyMode hoisted outside item loop
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int it = 0; it < iterations; it++)
            {
                bool legacyMode = _cachedLegacy;
                for (int i = 0; i < count; i++)
                {
                    _ = BuildItem_After("Test", "Procedure", "Desc", "Parent", "Module", "Path", "ParentPath", "Root", false, DateTime.UtcNow, legacyMode);
                }
            }
            sw.Stop();
            int gen0After = GC.CollectionCount(0) - gen0Start;
            double msAfter = sw.Elapsed.TotalMilliseconds / iterations;

            string report = $@"
=== BUILD_ITEM_BENCHMARK ===
Building 200 list items (x 2,000 iterations):
  ANTES (GetEnvironmentVariable per row): {msBefore:F3} ms/page, Gen0 Collections: {gen0Before}
  DEPOIS (Hoisted legacyMode):             {msAfter:F3} ms/page, Gen0 Collections: {gen0After}
============================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
