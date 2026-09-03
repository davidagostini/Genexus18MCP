using System;
using System.Diagnostics;
using GxMcp.Worker.Services;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class IndexShardBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public IndexShardBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static int FastShardOf(string storageKey)
        {
            if (string.IsNullOrEmpty(storageKey)) return 0;
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < storageKey.Length; i++)
                {
                    char c = storageKey[i];
                    char upper = (c >= 'a' && c <= 'z') ? (char)(c - 32) : char.ToUpperInvariant(c);
                    hash ^= upper;
                    hash *= 16777619;
                }
                return (int)(hash % IndexCacheService.ShardCount);
            }
        }

        [Fact]
        public void Benchmark_ShardOf_Throughput()
        {
            string[] keys = new[]
            {
                "Procedure:PCalculaDesconto",
                "Transaction:CustomerInvoiceHeader",
                "WebPanel:WPReportGeneralSalesSummary",
                "Domain:CustomerStatusDomain",
                "Table:InvoiceDetailsTableItem",
                "Attribute:InvoiceTotalCalculatedAmount"
            };

            // Assert equality for all sample keys
            foreach (var k in keys)
            {
                Assert.Equal(IndexCacheService.ShardOf(k), FastShardOf(k));
            }

            // Warmup
            for (int i = 0; i < 1000; i++)
            {
                _ = IndexCacheService.ShardOf(keys[i % keys.Length]);
                _ = FastShardOf(keys[i % keys.Length]);
            }

            int iterations = 200000;

            // ANTES: ShardOf with char.ToUpperInvariant and foreach
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                _ = IndexCacheService.ShardOf(keys[i % keys.Length]);
            }
            sw.Stop();
            int gen0Before = GC.CollectionCount(0) - gen0Start;
            double usBefore = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;

            // DEPOIS: FastShardOf with ASCII fast path and indexed loop
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < iterations; i++)
            {
                _ = FastShardOf(keys[i % keys.Length]);
            }
            sw.Stop();
            int gen0After = GC.CollectionCount(0) - gen0Start;
            double usAfter = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;

            string report = $@"
=== SHARD_OF_BENCHMARK ===
ShardOf 200,000 keys:
  ANTES (char.ToUpperInvariant foreach): {usBefore:F3} us/op, Gen0: {gen0Before}
  DEPOIS (ASCII fast-path indexed loop): {usAfter:F3} us/op, Gen0: {gen0After}
==========================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
