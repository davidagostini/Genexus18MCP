using BenchmarkDotNet.Attributes;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using System.Reflection;

namespace GxMcp.Benchmarks
{
    [MemoryDiagnoser]
    public class KbMetadataBenchmark
    {
        private JObject _clonePayload = null!;
        private JObject _ownedPayload = null!;
        private MethodInfo _cloneMethod = null!;
        private MethodInfo _ownedMethod = null!;

        [GlobalSetup]
        public void Setup()
        {
            var rows = new JArray();
            for (int i = 0; i < 500; i++)
            {
                rows.Add(new JObject
                {
                    ["name"] = $"Obj{i}",
                    ["type"] = "Procedure",
                    ["path"] = $"Folder/Obj{i}",
                    ["source"] = new string('x', 240)
                });
            }

            _clonePayload = new JObject { ["results"] = rows };
            _ownedPayload = (JObject)_clonePayload.DeepClone();
            _cloneMethod = typeof(GxMcp.Gateway.Program).GetMethod(
                "AddKbContextMetadata",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            _ownedMethod = typeof(GxMcp.Gateway.Program).GetMethod(
                "AttachKbContextMetadataToOwnedPayload",
                BindingFlags.NonPublic | BindingFlags.Static)!;
        }

        [Benchmark(Baseline = true)]
        public JToken CloneAndAttach()
        {
            return (JToken)_cloneMethod.Invoke(null, new object[] { _clonePayload, "customer" })!;
        }

        [Benchmark]
        public JToken AttachToOwnedPayload()
        {
            return (JToken)_ownedMethod.Invoke(null, new object[] { _ownedPayload, "customer" })!;
        }
    }
}
