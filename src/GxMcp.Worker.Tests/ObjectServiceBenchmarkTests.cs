using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class ObjectServiceBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public ObjectServiceBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Benchmark_ReadCacheKey_And_MatchOrdering_Throughput()
        {
            var guid = Guid.NewGuid();
            // Warmup
            for (int i = 0; i < 100; i++)
            {
                ObjectService.BuildReadCacheKey(guid, "Source", 0, 50, "mcp", false);
            }

            int iterations = 10000;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                ObjectService.BuildReadCacheKey(guid, "Source", 0, 50, "mcp", false);
                ObjectService.BuildReadCacheKey(guid, "Rules", null, null, "mcp", true);
            }
            sw.Stop();
            int gen0Count = GC.CollectionCount(0) - gen0Start;
            double usPerPair = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;

            string report = $@"
=== OBJECT_SERVICE_BENCHMARK ===
BuildReadCacheKey (10,000 pairs):
  Latency: {usPerPair:F2} us/pair
  Gen0 Collections: {gen0Count}
================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
