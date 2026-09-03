using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class SearchRankingBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public SearchRankingBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static int CompareRankedResults(SearchService.RankedResult a, SearchService.RankedResult b)
        {
            int scoreComp = b.Score.CompareTo(a.Score);
            if (scoreComp != 0) return scoreComp;

            string nameA = a.Entry != null ? a.Entry.Name : string.Empty;
            string nameB = b.Entry != null ? b.Entry.Name : string.Empty;
            return string.Compare(nameA ?? string.Empty, nameB ?? string.Empty, StringComparison.Ordinal);
        }

        [Fact]
        public void Benchmark_SearchRanking_Throughput()
        {
            int resultCount = 1000;
            var list = new List<SearchService.RankedResult>(resultCount);
            for (int i = 0; i < resultCount; i++)
            {
                var entry = new SearchIndex.IndexEntry
                {
                    Name = "Object_" + (resultCount - i),
                    Type = "Procedure",
                    Guid = Guid.NewGuid().ToString()
                };
                list.Add(new SearchService.RankedResult(entry, (i * 37) % 500, 0.5f));
            }

            int iterations = 2000;
            int limit = 20;

            // Warmup
            for (int i = 0; i < 10; i++)
            {
                var copy1 = new List<SearchService.RankedResult>(list);
                var sorted1 = copy1.OrderByDescending(r => r.Score).ThenBy(r => r.Entry.Name).ToList();
                _ = sorted1.Skip(0).Take(limit).ToList();

                var copy2 = new List<SearchService.RankedResult>(list);
                copy2.Sort(CompareRankedResults);
            }

            // ANTES: LINQ OrderByDescending.ThenBy.ToList + Skip.Take.ToList
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int it = 0; it < iterations; it++)
            {
                var copy = new List<SearchService.RankedResult>(list);
                var sorted = copy.OrderByDescending(r => r.Score).ThenBy(r => r.Entry.Name).ToList();
                var page = sorted.Skip(0).Take(limit).ToList();
            }
            sw.Stop();
            int gen0Before = GC.CollectionCount(0) - gen0Start;
            double msBefore = sw.Elapsed.TotalMilliseconds / iterations;

            // DEPOIS: In-place Sort(CompareRankedResults) + direct indexed bounds
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int it = 0; it < iterations; it++)
            {
                var copy = new List<SearchService.RankedResult>(list);
                copy.Sort(CompareRankedResults);
                int total = copy.Count;
                int startIndex = 0;
                int effectiveLimit = limit <= 0 ? total : limit;
                int endIndex = Math.Min(total, (int)Math.Min((long)total, (long)startIndex + effectiveLimit));
                // Direct iteration from startIndex to endIndex (zero list allocations)
            }
            sw.Stop();
            int gen0After = GC.CollectionCount(0) - gen0Start;
            double msAfter = sw.Elapsed.TotalMilliseconds / iterations;

            string report = $@"
=== SEARCH_RANKING_BENCHMARK ===
Ranking 1,000 search candidates with limit=20 (x 2,000 iterations):
  ANTES (LINQ OrderBy.ThenBy.ToList + Skip.Take.ToList): {msBefore:F3} ms/search, Gen0 Collections: {gen0Before}
  DEPOIS (In-place Sort + direct indexed bounds):         {msAfter:F3} ms/search, Gen0 Collections: {gen0After}
================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
