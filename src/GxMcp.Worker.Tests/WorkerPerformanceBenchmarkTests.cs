using System;
using System.Diagnostics;
using System.Text;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class WorkerPerformanceBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public WorkerPerformanceBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Benchmark_IsCacheableSuccessEnvelope_Throughput()
        {
            // 1. Small envelope (~50 bytes)
            string smallJson = "{\"status\":\"Ok\",\"target\":\"Customer\"}";

            // 2. Medium envelope (~50 KB)
            var mediumArr = new JArray();
            for (int i = 0; i < 200; i++)
            {
                mediumArr.Add(new JObject
                {
                    ["name"] = "Obj" + i,
                    ["type"] = "Procedure",
                    ["description"] = "Test description for object " + i,
                    ["lastUpdate"] = DateTime.UtcNow.ToString("O")
                });
            }
            string mediumJson = new JObject { ["status"] = "Ok", ["results"] = mediumArr }.ToString(Newtonsoft.Json.Formatting.None);

            // 3. Large envelope (~1 MB)
            var sb = new StringBuilder(1024 * 1024);
            sb.Append("{\"status\":\"Ok\",\"target\":\"BigProc\",\"code\":\"");
            for (int i = 0; i < 15000; i++)
            {
                sb.Append("For Each Customer Where CustomerId = ").Append(i).Append("\\n");
                sb.Append("  CustomerBalance += ").Append(i * 1.5).Append("\\n");
                sb.Append("EndFor\\n");
            }
            sb.Append("\"}");
            string largeJson = sb.ToString();

            // Warmup
            for (int i = 0; i < 10; i++)
            {
                CommandDispatcher.IsCacheableSuccessEnvelope(smallJson);
                CommandDispatcher.IsCacheableSuccessEnvelope(mediumJson);
                CommandDispatcher.IsCacheableSuccessEnvelope(largeJson);
            }

            // Benchmark Small (10,000 iterations)
            int smallIters = 10000;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long memStart = GC.GetTotalMemory(false);
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < smallIters; i++)
            {
                CommandDispatcher.IsCacheableSuccessEnvelope(smallJson);
            }
            sw.Stop();
            long memSmall = GC.GetTotalMemory(false) - memStart;
            int gen0Small = GC.CollectionCount(0) - gen0Start;
            double smallNsPerOp = (sw.Elapsed.TotalMilliseconds * 1000000.0) / smallIters;

            // Benchmark Medium (1,000 iterations)
            int medIters = 1000;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            memStart = GC.GetTotalMemory(false);
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < medIters; i++)
            {
                CommandDispatcher.IsCacheableSuccessEnvelope(mediumJson);
            }
            sw.Stop();
            long memMed = GC.GetTotalMemory(false) - memStart;
            int gen0Med = GC.CollectionCount(0) - gen0Start;
            double medUsPerOp = (sw.Elapsed.TotalMilliseconds * 1000.0) / medIters;

            // Benchmark Large (100 iterations)
            int largeIters = 100;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            memStart = GC.GetTotalMemory(false);
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < largeIters; i++)
            {
                CommandDispatcher.IsCacheableSuccessEnvelope(largeJson);
            }
            sw.Stop();
            long memLarge = GC.GetTotalMemory(false) - memStart;
            int gen0Large = GC.CollectionCount(0) - gen0Start;
            double largeMsPerOp = sw.Elapsed.TotalMilliseconds / largeIters;

            string report = $@"
=== IS_CACHEABLE_BENCHMARK ===
Small (~50 B): {smallNsPerOp:F1} ns/op, Gen0 Collections: {gen0Small}
Medium (~50 KB): {medUsPerOp:F2} us/op, Gen0 Collections: {gen0Med}
Large (~1 MB): {largeMsPerOp:F2} ms/op, Gen0 Collections: {gen0Large}
==============================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
