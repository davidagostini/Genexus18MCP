using System;
using System.Diagnostics;
using System.IO;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Gateway.Tests
{
    public class ToolProfileBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public ToolProfileBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Benchmark_ToolProfileFilter_Throughput()
        {
            string candidate = Path.Combine(
                Path.GetDirectoryName(typeof(Program).Assembly.Location)!,
                "tool_definitions.json");
            if (!File.Exists(candidate))
            {
                candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "GxMcp.Gateway", "tool_definitions.json");
            }
            if (!File.Exists(candidate))
            {
                candidate = "C:\\Projetos\\Genexus18MCP\\src\\GxMcp.Gateway\\tool_definitions.json";
            }

            string json = File.ReadAllText(candidate);
            var tools = JArray.Parse(json);

            // Warmup
            for (int i = 0; i < 50; i++)
            {
                ToolProfileFilter.Filter(tools, "core");
                ToolProfileFilter.Filter(tools, "authoring");
            }

            int iterations = 10000;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long memStart = GC.GetTotalMemory(false);
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                ToolProfileFilter.Filter(tools, "core");
            }
            sw.Stop();
            int gen0Core = GC.CollectionCount(0) - gen0Start;
            double coreUsPerOp = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;

            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < iterations; i++)
            {
                ToolProfileFilter.Filter(tools, "authoring");
            }
            sw.Stop();
            int gen0Auth = GC.CollectionCount(0) - gen0Start;
            double authUsPerOp = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;

            // Cached GetOrCreateFiltered benchmark (100,000 iterations)
            int cachedIters = 100000;
            ToolProfileFilter.GetOrCreateFiltered(tools, "core");
            ToolProfileFilter.GetOrCreateFiltered(tools, "authoring");

            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < cachedIters; i++)
            {
                ToolProfileFilter.GetOrCreateFiltered(tools, "core");
            }
            sw.Stop();
            int gen0CachedCore = GC.CollectionCount(0) - gen0Start;
            double cachedCoreNsPerOp = (sw.Elapsed.TotalMilliseconds * 1000000.0) / cachedIters;

            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < cachedIters; i++)
            {
                ToolProfileFilter.GetOrCreateFiltered(tools, "authoring");
            }
            sw.Stop();
            int gen0CachedAuth = GC.CollectionCount(0) - gen0Start;
            double cachedAuthNsPerOp = (sw.Elapsed.TotalMilliseconds * 1000000.0) / cachedIters;

            string report = $@"
=== TOOL_PROFILE_FILTER_BENCHMARK ===
ANTES (Uncached):
  Filter 'core' (11 tools): {coreUsPerOp:F2} us/op, Gen0 Collections (10k ops): {gen0Core}
  Filter 'authoring' (29 tools): {authUsPerOp:F2} us/op, Gen0 Collections (10k ops): {gen0Auth}
DEPOIS (Cached GetOrCreateFiltered):
  GetOrCreateFiltered 'core': {cachedCoreNsPerOp:F1} ns/op, Gen0 Collections (100k ops): {gen0CachedCore}
  GetOrCreateFiltered 'authoring': {cachedAuthNsPerOp:F1} ns/op, Gen0 Collections (100k ops): {gen0CachedAuth}
=====================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
