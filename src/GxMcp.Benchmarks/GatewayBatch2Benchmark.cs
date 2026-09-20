using BenchmarkDotNet.Attributes;
using Newtonsoft.Json.Linq;
using GxMcp.Gateway;

namespace GxMcp.Benchmarks
{
    [MemoryDiagnoser]
    public class GatewayBatch2Benchmark
    {
        private JObject _canonicalArgs = null!;
        private JObject _largeEditArgs = null!;

        [GlobalSetup]
        public void Setup()
        {
            _canonicalArgs = new JObject
            {
                ["query"] = "Customer",
                ["typeFilter"] = "Transaction",
                ["limit"] = 50
            };

            var targets = new JArray();
            for (int i = 0; i < 50; i++)
            {
                targets.Add(new JObject
                {
                    ["name"] = $"Transaction{i}",
                    ["part"] = "Source",
                    ["code"] = $"// Source code for object {i}\nFor each Customer\n  CustomerTotal += InvoiceTotal\nEndfor\n"
                });
            }

            _largeEditArgs = new JObject
            {
                ["idempotencyKey"] = "idem-key-12345",
                ["dryRun"] = false,
                ["targets"] = targets,
                ["comment"] = "Batch update 50 transactions"
            };
        }

        [Benchmark]
        public bool TryRewriteLegacyTool_CanonicalMiss()
        {
            return McpRouter.TryRewriteLegacyTool("genexus_query", _canonicalArgs, out _, out _);
        }

        [Benchmark]
        public bool TryRewriteLegacyTool_LargeEditMiss()
        {
            return McpRouter.TryRewriteLegacyTool("genexus_edit", _largeEditArgs, out _, out _);
        }

        [Benchmark]
        public bool TryRewriteLegacyTool_LegacyHit()
        {
            return McpRouter.TryRewriteLegacyTool("genexus_smoke_test", _canonicalArgs, out _, out _);
        }
    }
}
