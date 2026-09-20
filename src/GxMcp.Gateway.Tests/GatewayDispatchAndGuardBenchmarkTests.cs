using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace GxMcp.Gateway.Tests
{
    public class GatewayDispatchAndGuardBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public GatewayDispatchAndGuardBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // Implementation of CountingStream (existing in ResponseSizeGuard)
        private sealed class CountingStreamOld : Stream
        {
            public long Count;
            public override bool CanWrite => true;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override long Length => Count;
            public override long Position { get => Count; set => throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) => Count += count;
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private static long ByteSize_StreamWriter(JToken token)
        {
            var counter = new CountingStreamOld();
            using (var writer = new StreamWriter(counter, Utf8NoBom, bufferSize: 32 * 1024, leaveOpen: true) { AutoFlush = false })
            using (var jw = new JsonTextWriter(writer) { Formatting = Formatting.None })
            {
                token.WriteTo(jw);
                jw.Flush();
                writer.Flush();
            }
            return counter.Length;
        }

        // Reusable thread-static counting stream + writer
        private sealed class CountingContext
        {
            public readonly CountingStreamOld Stream = new CountingStreamOld();
            public readonly StreamWriter Writer;

            public CountingContext()
            {
                Writer = new StreamWriter(Stream, Utf8NoBom, bufferSize: 32 * 1024, leaveOpen: true) { AutoFlush = false };
            }

            public void Reset()
            {
                Stream.Count = 0;
            }
        }

        [ThreadStatic]
        private static CountingContext? t_countingContext;

        private static long ByteSize_Reusable(JToken token)
        {
            if (token == null) return 0;
            var ctx = t_countingContext ??= new CountingContext();
            ctx.Reset();
            using (var jw = new JsonTextWriter(ctx.Writer) { Formatting = Formatting.None, CloseOutput = false })
            {
                token.WriteTo(jw);
                jw.Flush();
                ctx.Writer.Flush();
            }
            return ctx.Stream.Length;
        }

        [Fact]
        public void Benchmark_ByteSize_Calculation()
        {
            var testObject = new JObject
            {
                ["status"] = "ok",
                ["query"] = "Invoice",
                ["items"] = new JArray(Enumerable.Range(0, 50).Select(i => new JObject
                {
                    ["guid"] = Guid.NewGuid().ToString(),
                    ["name"] = "InvoiceItem" + i,
                    ["type"] = "Transaction",
                    ["description"] = "Nota Fiscal Eletrônica e transações de clientes"
                }))
            };

            // Verify parity
            long sizeOld = ByteSize_StreamWriter(testObject);
            long sizeNew = ByteSize_Reusable(testObject);
            long sizeExact = Encoding.UTF8.GetByteCount(testObject.ToString(Formatting.None));
            Assert.Equal(sizeExact, sizeOld);
            Assert.Equal(sizeExact, sizeNew);

            int iterations = 10000;

            // Warmup
            for (int i = 0; i < 100; i++)
            {
                _ = ByteSize_StreamWriter(testObject);
                _ = ByteSize_Reusable(testObject);
            }

            // Benchmark OLD
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                _ = ByteSize_StreamWriter(testObject);
            }
            sw.Stop();
            int gen0Before = GC.CollectionCount(0) - gen0Start;
            double usBefore = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;

            // Benchmark NEW
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < iterations; i++)
            {
                _ = ByteSize_Reusable(testObject);
            }
            sw.Stop();
            int gen0After = GC.CollectionCount(0) - gen0Start;
            double usAfter = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;

            string report = $@"
=== BYTE_SIZE_GUARD_BENCHMARK ===
10,000 JSON Response Payload Checks (~6KB payload):
  ANTES (New StreamWriter + 32KB buffer): {usBefore:F2} us/op, Gen0 Collections: {gen0Before}
  DEPOIS (ThreadStatic Reusable Context):  {usAfter:F2} us/op, Gen0 Collections: {gen0After}
=================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }

        [Fact]
        public void Benchmark_ToolRouting_Dispatch()
        {
            var testTools = new[] { "genexus_query", "genexus_read", "genexus_analyze", "genexus_kb", "genexus_create", "genexus_edit", "genexus_custom_declarative" };
            var fakeDefs = new JArray(testTools.Select(t => new JObject { ["name"] = t }));

            // Router simulation
            var routerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["genexus_query"] = "SearchRouter",
                ["genexus_read"] = "ObjectRouter",
                ["genexus_analyze"] = "AnalyzeRouter",
                ["genexus_kb"] = "SystemRouter",
                ["genexus_create"] = "CreateRouter",
                ["genexus_edit"] = "ObjectRouter"
            };

            var routerList = routerMap.Select(kv => kv).ToList();
            var declaredSet = new HashSet<string>(testTools, StringComparer.OrdinalIgnoreCase);

            int iterations = 100000;

            // Warmup
            foreach (var t in testTools)
            {
                _ = routerList.FirstOrDefault(r => string.Equals(r.Key, t, StringComparison.OrdinalIgnoreCase)).Value;
                _ = routerMap.TryGetValue(t, out _);
                _ = fakeDefs.Any(d => string.Equals(d["name"]?.ToString(), t, StringComparison.OrdinalIgnoreCase));
                _ = declaredSet.Contains(t);
            }

            // ANTES: Sequential router search + JArray.Any LINQ
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Start = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                string tool = testTools[i % testTools.Length];
                string? routed = null;
                foreach (var r in routerList)
                {
                    if (string.Equals(r.Key, tool, StringComparison.OrdinalIgnoreCase))
                    {
                        routed = r.Value;
                        break;
                    }
                }
                if (routed == null)
                {
                    bool isDeclared = fakeDefs.Any(d => string.Equals(d["name"]?.ToString(), tool, StringComparison.OrdinalIgnoreCase));
                    if (isDeclared) routed = "Declarative";
                }
            }
            sw.Stop();
            int gen0Before = GC.CollectionCount(0) - gen0Start;
            double nsBefore = (sw.Elapsed.TotalMilliseconds * 1000000.0) / iterations;

            // DEPOIS: Dictionary O(1) + HashSet O(1)
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            gen0Start = GC.CollectionCount(0);
            sw.Restart();
            for (int i = 0; i < iterations; i++)
            {
                string tool = testTools[i % testTools.Length];
                if (!routerMap.TryGetValue(tool, out var routed))
                {
                    if (declaredSet.Contains(tool))
                    {
                        routed = "Declarative";
                    }
                }
            }
            sw.Stop();
            int gen0After = GC.CollectionCount(0) - gen0Start;
            double nsAfter = (sw.Elapsed.TotalMilliseconds * 1000000.0) / iterations;

            string report = $@"
=== TOOL_ROUTING_DISPATCH_BENCHMARK ===
100,000 Tool Dispatch Resolutions:
  ANTES (Router loop + JArray.Any): {nsBefore:F1} ns/op, Gen0 Collections: {gen0Before}
  DEPOIS (Dictionary O(1) + HashSet): {nsAfter:F1} ns/op, Gen0 Collections: {gen0After}
=======================================";
            _output.WriteLine(report);
            Console.WriteLine(report);
        }
    }
}
