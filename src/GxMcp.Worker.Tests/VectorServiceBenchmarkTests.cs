using System;
using System.Diagnostics;
using GxMcp.Worker.Services;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Worker.Tests
{
    public class VectorServiceBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public VectorServiceBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void Benchmark_ComputeEmbedding_Throughput()
        {
            var svc = new VectorService();
            string text = "CustomerInvoice Transaction handles customer payments, monthly subscriptions, and regional tax calculation for billing accounts in production";

            // Warmup
            for (int i = 0; i < 100; i++)
            {
                svc.ComputeEmbedding(text);
            }

            int iterations = 10000;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                svc.ComputeEmbedding(text);
            }
            sw.Stop();
            int gen0Count = GC.CollectionCount(0) - gen0Start;
            double usPerOp = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;

            string report = $@"
=== VECTOR_EMBEDDING_BENCHMARK ===
ComputeEmbedding (10,000 iterations):
  Latency: {usPerOp:F2} us/op
  Gen0 Collections: {gen0Count}
==================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
