using BenchmarkDotNet.Attributes;
using Newtonsoft.Json.Linq;
using System.IO;
using GxMcp.Gateway;

namespace GxMcp.Benchmarks
{
    [MemoryDiagnoser]
    public class ToolProfileFilterBenchmark
    {
        private JArray _tools = null!;

        [GlobalSetup]
        public void Setup()
        {
            string candidate = Path.Combine(
                Path.GetDirectoryName(typeof(GxMcp.Gateway.Program).Assembly.Location)!,
                "tool_definitions.json");
            if (!File.Exists(candidate))
            {
                candidate = Path.Combine("..", "..", "..", "..", "GxMcp.Gateway", "tool_definitions.json");
            }
            string json = File.ReadAllText(candidate);
            _tools = JArray.Parse(json);
        }

        [Benchmark(Baseline = true)]
        public JArray FilterCore_Uncached()
        {
            return ToolProfileFilter.Filter(_tools, "core");
        }

        [Benchmark]
        public JArray FilterAuthoring_Uncached()
        {
            return ToolProfileFilter.Filter(_tools, "authoring");
        }
    }
}
