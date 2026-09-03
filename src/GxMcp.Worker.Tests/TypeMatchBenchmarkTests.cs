using System;
using System.Diagnostics;
using GxMcp.Worker.Services;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class TypeMatchBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public TypeMatchBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static bool IsTypeMatch_Before(string type, string query)
        {
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(query)) return false;
            string t = type.ToLower(); string q = query.ToLower();
            if (q == "prc" || q == "procedure" || q == "proc") return t.Contains("procedure");
            if (q == "trn" || q == "transaction") return t.Contains("transaction");
            if (q == "tab" || q == "table") return t == "table";
            if (q == "wp" || q == "webpanel") return t.Contains("webpanel");
            if (q == "dp" || q == "dataprovider") return t.Contains("dataprovider");
            if (q == "sdt") return t.Contains("sdt");
            if (q == "attr" || q == "attribute") return t.Contains("attribute");
            return t.Contains(q);
        }

        private static bool IsTypeMatch_After(string type, string query)
        {
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(query)) return false;

            if (string.Equals(query, "prc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "procedure", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "proc", StringComparison.OrdinalIgnoreCase))
            {
                return type.IndexOf("procedure", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (string.Equals(query, "trn", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "transaction", StringComparison.OrdinalIgnoreCase))
            {
                return type.IndexOf("transaction", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (string.Equals(query, "tab", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "table", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(type, "table", StringComparison.OrdinalIgnoreCase);
            }
            if (string.Equals(query, "wp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "webpanel", StringComparison.OrdinalIgnoreCase))
            {
                return type.IndexOf("webpanel", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (string.Equals(query, "dp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "dataprovider", StringComparison.OrdinalIgnoreCase))
            {
                return type.IndexOf("dataprovider", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (string.Equals(query, "sdt", StringComparison.OrdinalIgnoreCase))
            {
                return type.IndexOf("sdt", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (string.Equals(query, "attr", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(query, "attribute", StringComparison.OrdinalIgnoreCase))
            {
                return type.IndexOf("attribute", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return type.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [Fact]
        public void Benchmark_TypeMatch_KBScan()
        {
            string[] candidateTypes = new[]
            {
                "Procedure", "Transaction", "WebPanel", "Attribute", "Table",
                "Domain", "Folder", "Module", "DataProvider", "SDT"
            };

            string[] queries = new[] { "prc", "trn", "tab", "wp", "dp", "sdt", "attr", "Domain", "custom" };

            // Verify parity
            foreach (var t in candidateTypes)
            {
                foreach (var q in queries)
                {
                    Assert.Equal(IsTypeMatch_Before(t, q), IsTypeMatch_After(t, q));
                }
            }

            int kbObjectCount = 38000;
            int iterations = 100;

            // Warmup
            for (int i = 0; i < 1000; i++)
            {
                _ = IsTypeMatch_Before(candidateTypes[i % candidateTypes.Length], "prc");
                _ = IsTypeMatch_After(candidateTypes[i % candidateTypes.Length], "prc");
            }

            // ANTES: ToLower on type and query
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int it = 0; it < iterations; it++)
            {
                string q = queries[it % queries.Length];
                for (int i = 0; i < kbObjectCount; i++)
                {
                    _ = IsTypeMatch_Before(candidateTypes[i % candidateTypes.Length], q);
                }
            }
            sw.Stop();
            int gen0Before = GC.CollectionCount(0) - gen0Start;
            double msBefore = sw.Elapsed.TotalMilliseconds / iterations;

            // DEPOIS: Zero-allocation string.Equals / IndexOf
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int it = 0; it < iterations; it++)
            {
                string q = queries[it % queries.Length];
                for (int i = 0; i < kbObjectCount; i++)
                {
                    _ = IsTypeMatch_After(candidateTypes[i % candidateTypes.Length], q);
                }
            }
            sw.Stop();
            int gen0After = GC.CollectionCount(0) - gen0Start;
            double msAfter = sw.Elapsed.TotalMilliseconds / iterations;

            string report = $@"
=== TYPE_MATCH_BENCHMARK ===
38,000 KB objects type match scan (x 100 iterations):
  ANTES (type.ToLower() + query.ToLower()): {msBefore:F3} ms/scan, Gen0 Collections: {gen0Before}
  DEPOIS (Zero-alloc OrdinalIgnoreCase):     {msAfter:F3} ms/scan, Gen0 Collections: {gen0After}
============================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
