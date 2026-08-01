using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class Issue58To62RoutingTests
    {
        [Fact]
        public void ApplyPattern_ActionsRoutesToTypedManager()
        {
            var routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_apply_pattern", new JObject
            {
                ["name"] = "Customer", ["pattern"] = "WorkWithPlus", ["mode"] = "actions",
                ["action"] = "list_actions"
            }));
            Assert.Equal("Pattern", (string)routed["module"]);
            Assert.Equal("ManageActions", (string)routed["action"]);
        }

        [Fact]
        public void Create_ObjectAtomicRoutesWholePayload()
        {
            var routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_create", new JObject
            {
                ["action"] = "object_atomic", ["name"] = "P", ["type"] = "Procedure",
                ["validate"] = true
            }));
            Assert.Equal("AtomicCreate", (string)routed["module"]);
            Assert.Equal("P", (string)routed["params"]?["name"]);
            Assert.True((bool)routed["params"]?["validate"]);
        }

        [Fact]
        public void Db_ReorgPreviewRoutesToNonMutatingImpactService()
        {
            var routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_db", new JObject
            { ["action"] = "reorg_preview", ["deep"] = true }));
            Assert.Equal("ReorgImpact", (string)routed["module"]);
            Assert.Equal("reorg_preview", (string)routed["params"]?["action"]);
        }
    }
}
