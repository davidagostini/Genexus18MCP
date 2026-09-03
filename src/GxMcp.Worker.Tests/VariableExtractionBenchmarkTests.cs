using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class VariableExtractionBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public VariableExtractionBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static (List<string> varNames, HashSet<string> sdtCandidates) Extract_Before(string scanCode)
        {
            var matches = Regex.Matches(scanCode, @"&(\w+)");
            var varNames = matches.Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            var sdtCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(scanCode, @"&(\w+)\."))
            {
                sdtCandidates.Add(m.Groups[1].Value);
            }

            return (varNames, sdtCandidates);
        }

        private static bool IsWordChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
        }

        private static (List<string> varNames, HashSet<string> sdtCandidates) Extract_After(string scanCode)
        {
            if (string.IsNullOrEmpty(scanCode))
                return (new List<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            var varNamesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var varNamesList = new List<string>();
            var sdtCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int n = scanCode.Length;
            int i = 0;
            while (i < n)
            {
                if (scanCode[i] == '&')
                {
                    int start = i + 1;
                    int j = start;
                    while (j < n && IsWordChar(scanCode[j]))
                    {
                        j++;
                    }

                    if (j > start)
                    {
                        string name = scanCode.Substring(start, j - start);
                        if (varNamesSet.Add(name))
                        {
                            varNamesList.Add(name);
                        }

                        if (j < n && scanCode[j] == '.')
                        {
                            sdtCandidates.Add(name);
                        }

                        i = j;
                        continue;
                    }
                }
                i++;
            }

            return (varNamesList, sdtCandidates);
        }

        [Fact]
        public void Benchmark_VariableExtraction_Throughput()
        {
            string sampleCode = @"
                // Procedimento de calculo de comissao e desconto
                &TotalDesconto = 0
                &CliId = &CustomerId
                &CustomerSDT.Load(&CliId)
                For Each Order
                    Where OrderCustomerId = &CliId
                    Where OrderDate >= &DateFrom
                    &OrderTotal = OrderAmount - OrderDiscount
                    &TotalDesconto += &OrderTotal * &DescontoPercentual / 100
                    &OrderList.Add(&OrderTotal)
                EndFor
                &Invoice.Header.Amount = &TotalDesconto
                &Invoice.Save()
                Commit
            ";

            // Verify parity
            var before = Extract_Before(sampleCode);
            var after = Extract_After(sampleCode);

            Assert.Equal(before.varNames.OrderBy(v => v), after.varNames.OrderBy(v => v));
            Assert.Equal(before.sdtCandidates.OrderBy(v => v), after.sdtCandidates.OrderBy(v => v));

            int iterations = 10000;

            // Warmup
            for (int i = 0; i < 100; i++)
            {
                _ = Extract_Before(sampleCode);
                _ = Extract_After(sampleCode);
            }

            // ANTES: Two uncompiled Regexes + LINQ
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int it = 0; it < iterations; it++)
            {
                _ = Extract_Before(sampleCode);
            }
            sw.Stop();
            int gen0Before = GC.CollectionCount(0) - gen0Start;
            double msBefore = sw.Elapsed.TotalMilliseconds / iterations;

            // DEPOIS: Single-pass linear scan
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int it = 0; it < iterations; it++)
            {
                _ = Extract_After(sampleCode);
            }
            sw.Stop();
            int gen0After = GC.CollectionCount(0) - gen0Start;
            double msAfter = sw.Elapsed.TotalMilliseconds / iterations;

            string report = $@"
=== VARIABLE_EXTRACTION_BENCHMARK ===
Extract variables from Procedure source (x 10,000 iterations):
  ANTES (Two Regex passes + LINQ Cast/Distinct): {msBefore:F3} ms/op, Gen0 Collections: {gen0Before}
  DEPOIS (Single-pass linear scan):              {msAfter:F3} ms/op, Gen0 Collections: {gen0After}
=====================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
