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
            })!);
            Assert.Equal("AtomicCreate", (string)routed["module"]);
            Assert.Equal("P", (string)routed["params"]?["name"]);
            Assert.True((bool)routed["params"]?["validate"]);
        }

        [Fact]
        public void Create_OmittedAction_WithSource_RoutesToAtomicCreate()
        {
            var routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_create", new JObject
            {
                ["name"] = "P", ["type"] = "Procedure", ["source"] = "msg('hi');"
            })!);
            Assert.Equal("AtomicCreate", (string)routed["module"]);
            Assert.Equal("P", (string)routed["params"]?["name"]);
            Assert.Equal("msg('hi');", (string)routed["params"]?["source"]);
        }

        [Fact]
        public void Create_OmittedAction_WithTypeAndName_RoutesToObjectCreate()
        {
            var routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_create", new JObject
            {
                ["name"] = "Customer", ["type"] = "Transaction"
            })!);
            Assert.Equal("Object", (string)routed["module"]);
            Assert.Equal("Create", (string)routed["action"]);
            Assert.Equal("Customer", (string)routed["target"]);
            Assert.Equal("Transaction", (string)routed["type"]);
        }

        [Fact]
        public void Db_ReorgPreviewRoutesToNonMutatingImpactService()
        {
            var routed = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_db", new JObject
            { ["action"] = "reorg_preview", ["deep"] = true })!);
            Assert.Equal("ReorgImpact", (string)routed["module"]);
            Assert.Equal("reorg_preview", (string)routed["params"]?["action"]);
        }
    }
}
