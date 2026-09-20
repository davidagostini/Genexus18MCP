using System;
using System.Diagnostics;
using GxMcp.Worker.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class QueryGrammarBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public QueryGrammarBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Benchmark_QueryGrammar_Parse_Throughput()
        {
            string plainQuery = "Customer Invoice Total Billing";
            string structuredQuery = "type:Procedure customer usedby:Invoice description:\"billing processor\"";

            // Warmup
            for (int i = 0; i < 100; i++)
            {
                QueryGrammar.Parse(plainQuery);
                QueryGrammar.Parse(structuredQuery);
            }

            int iterations = 10000;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                QueryGrammar.Parse(plainQuery);
                QueryGrammar.Parse(structuredQuery);
            }
            sw.Stop();
            int gen0Count = GC.CollectionCount(0) - gen0Start;
            double usPerPair = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;

            string report = $@"
=== QUERY_GRAMMAR_BENCHMARK ===
Parse (10,000 plain + 10,000 structured queries):
  Latency: {usPerPair:F2} us/pair
  Gen0 Collections: {gen0Count}
===============================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
