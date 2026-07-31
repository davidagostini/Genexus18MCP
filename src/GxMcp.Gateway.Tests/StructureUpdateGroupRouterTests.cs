using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // issue #54: genexus_structure action=update_group must reach the worker's
    // UpdateGroupStructure action with the Group name as target and the members/
    // remove payload intact — the same way the other structure actions route.
    public class StructureUpdateGroupRouterTests
    {
        private static JObject Route(JObject args)
        {
            var routed = new OperationsRouter().ConvertToolCall("genexus_structure", args);
            Assert.NotNull(routed);
            return JObject.FromObject(routed!);
        }

        [Fact]
        public void UpdateGroup_ForwardsGroupNameAndPayload()
        {
            var args = new JObject
            {
                ["action"] = "update_group",
                ["name"] = "gst_orgao_exercicio",
                ["payload"] = new JObject
                {
                    ["members"] = new JArray
                    {
                        new JObject { ["name"] = "orgao_exercicio_id", ["subtypeOf"] = "exercicio_id" },
                        new JObject { ["name"] = "orgao_exercicio", ["subtypeOf"] = "exercicio" }
                    }
                }
            };

            var jo = Route(args);
            Assert.Equal("Structure", jo["module"]!.ToString());
            Assert.Equal("UpdateGroupStructure", jo["action"]!.ToString());
            Assert.Equal("gst_orgao_exercicio", jo["target"]!.ToString());

            var payload = JObject.Parse(jo["payload"]!.ToString());
            var members = payload["members"] as JArray;
            Assert.NotNull(members);
            Assert.Equal(2, members!.Count);
            Assert.Equal("orgao_exercicio_id", members[0]!["name"]!.ToString());
            Assert.Equal("exercicio_id", members[0]!["subtypeOf"]!.ToString());
        }

        [Fact]
        public void UpdateGroup_ForwardsRemoveList()
        {
            var args = new JObject
            {
                ["action"] = "update_group",
                ["name"] = "gst_orgao_exercicio",
                ["payload"] = new JObject { ["remove"] = new JArray { "orgao_exercicio" } }
            };

            var jo = Route(args);
            Assert.Equal("UpdateGroupStructure", jo["action"]!.ToString());
            var payload = JObject.Parse(jo["payload"]!.ToString());
            var remove = payload["remove"] as JArray;
            Assert.NotNull(remove);
            Assert.Equal("orgao_exercicio", remove![0]!.ToString());
        }

        [Fact]
        public void UnknownStructureAction_ReturnsNull()
        {
            var args = new JObject { ["action"] = "update_banana", ["name"] = "X" };
            Assert.Null(new OperationsRouter().ConvertToolCall("genexus_structure", args));
        }
    }
}
