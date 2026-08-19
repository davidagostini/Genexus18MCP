using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class DataViewRoutingTests
    {
        [Fact]
        public void DataView_ForwardsTypedAtomicPayloadWithoutLifecycleAction()
        {
            var args = new JObject
            {
                ["action"] = "dry_run",
                ["transaction"] = "LedgerEntryView",
                ["dataViewName"] = "LedgerEntryDV",
                ["dataStore"] = "Default",
                ["schema"] = "APP",
                ["table"] = "LEDGERENTRY",
                ["expectedVersion"] = "v1",
                ["dryRun"] = true,
                ["rollbackOnFailure"] = true,
                ["attributeMappings"] = new JArray(
                    new JObject { ["attribute"] = "LedgerEntryId", ["column"] = "LedgerEntryId", ["key"] = true })
            };

            JObject routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_data_view", args)!);

            Assert.Equal("DataView", routed["module"]?.ToString());
            Assert.Equal("Run", routed["action"]?.ToString());
            Assert.Equal("LedgerEntryView", routed["target"]?.ToString());
            Assert.Equal("dry_run", routed["params"]?["action"]?.ToString());
            Assert.Equal("v1", routed["params"]?["expectedVersion"]?.ToString());
            Assert.True(routed["params"]?["rollbackOnFailure"]?.ToObject<bool>());
            Assert.Null(routed["lifecycle"]);
        }
    }
}
