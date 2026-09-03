using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Gateway.Tests
{
    public class DiscoveryResponseBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public DiscoveryResponseBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Benchmark_Discovery_Endpoints_Throughput()
        {
            var reqResList = new JObject { ["method"] = "resources/list" };
            var reqTemplates = new JObject { ["method"] = "resources/templates/list" };
            var reqPrompts = new JObject { ["method"] = "prompts/list" };

            // Warmup
            for (int i = 0; i < 100; i++)
            {
                McpRouter.Handle(reqResList);
                McpRouter.Handle(reqTemplates);
                McpRouter.Handle(reqPrompts);
            }

            int iterations = 10000;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                McpRouter.Handle(reqResList);
                McpRouter.Handle(reqTemplates);
                McpRouter.Handle(reqPrompts);
            }
            sw.Stop();
            int gen0Count = GC.CollectionCount(0) - gen0Start;
            double usPerSet = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;

            string report = $@"
=== DISCOVERY_RESPONSE_BENCHMARK ===
Handle 3 discovery calls (resources/list, templates/list, prompts/list) x 10,000:
  Latency: {usPerSet:F2} us/set
  Gen0 Collections: {gen0Count}
====================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
