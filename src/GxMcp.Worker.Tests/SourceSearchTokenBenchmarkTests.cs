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
    public class SourceSearchTokenBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public SourceSearchTokenBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Benchmark_LiteralToken_And_PreFilter_Throughput()
        {
            var entries = new List<SearchIndex.IndexEntry>(10000);
            for (int i = 0; i < 10000; i++)
            {
                entries.Add(new SearchIndex.IndexEntry
                {
                    Name = "CustomerPaymentProc" + i,
                    Type = "Procedure",
                    SourceSnippet = i % 3 == 0 ? "For Each Customer Where CustomerId = &CustomerId" : null,
                    Keywords = new List<string> { "customer", "payment", "invoice" }
                });
            }

            string pattern = @"CustomerPaymentProc\w+";
            string callee = "ProcessInvoicePayment";

            // Warmup
            for (int i = 0; i < 100; i++)
            {
                var tokens = SourceSearchService.ExtractLiteralTokens(pattern, callee);
                SourceSearchService.MatchesAnyLiteral(entries[0], tokens);
            }

            int iterations = 1000;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var tokens = SourceSearchService.ExtractLiteralTokens(pattern, callee);
                // Check 100 candidates per iteration
                for (int c = 0; c < 100; c++)
                {
                    SourceSearchService.MatchesAnyLiteral(entries[c], tokens);
                }
            }
            sw.Stop();
            int gen0Count = GC.CollectionCount(0) - gen0Start;
            double usPerOp = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;

            string report = $@"
=== SOURCE_SEARCH_TOKEN_BENCHMARK ===
ExtractLiteralTokens + 100 MatchesAnyLiteral (1,000 iterations):
  Latency: {usPerOp:F2} us/op
  Gen0 Collections: {gen0Count}
=====================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
