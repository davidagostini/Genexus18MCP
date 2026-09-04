using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // issue #34: the gateway dropped `type` on genexus_edit mode=patch/ops (so the worker
    // re-resolved a homonym Transaction/Table by name only → "Ambiguous object name" on write)
    // and dropped `objectName` on genexus_search_source (so the O(objects) fast path was dead).
    public class Issue34RouterForwardingTests
    {
        private static JObject Route(string tool, JObject args)
        {
            var routed = new ObjectRouter().ConvertToolCall(tool, args);
            Assert.NotNull(routed);
            return JObject.FromObject(routed!);
        }

        [Fact]
        public void EditPatch_TextForm_ForwardsType()
        {
            var args = new JObject
            {
                ["name"] = "Acao",
                ["type"] = "Transaction",
                ["part"] = "Structure",
                ["mode"] = "patch",
                ["operation"] = "Insert_After",
                ["context"] = "AcaoDes : VARCHAR(40)",
                ["content"] = "AcaoNew : NUMERIC(4)"
            };
            var jo = Route("genexus_edit", args);
            Assert.Equal("Patch", jo["module"]!.ToString());
            Assert.Equal("Transaction", jo["type"]!.ToString());
        }

        [Fact]
        public void EditPatch_FindReplaceForm_ForwardsType()
        {
            var args = new JObject
            {
                ["name"] = "Acao",
                ["type"] = "Transaction",
                ["part"] = "Structure",
                ["mode"] = "patch",
                ["patch"] = new JObject { ["find"] = "a", ["replace"] = "b" }
            };
            var jo = Route("genexus_edit", args);
            Assert.Equal("Patch", jo["module"]!.ToString());
            Assert.Equal("Transaction", jo["type"]!.ToString());
        }

        [Fact]
        public void EditPatch_JsonPatchArrayForm_ForwardsType()
        {
            var args = new JObject
            {
                ["name"] = "Acao",
                ["type"] = "Transaction",
                ["part"] = "Structure",
                ["mode"] = "patch",
                ["patch"] = new JArray { new JObject { ["op"] = "test", ["path"] = "/x", ["value"] = "y" } }
            };
            var jo = Route("genexus_edit", args);
            Assert.Equal("JsonPatch", jo["module"]!.ToString());
            Assert.Equal("Transaction", jo["type"]!.ToString());
        }

        [Fact]
        public void EditOps_ForwardsType()
        {
            var args = new JObject
            {
                ["name"] = "Acao",
                ["type"] = "Transaction",
                ["part"] = "Structure",
                ["mode"] = "ops",
                ["ops"] = new JArray { new JObject { ["op"] = "add_attribute", ["name"] = "A", ["type"] = "Numeric(4.0)" } }
            };
            var jo = Route("genexus_edit", args);
            Assert.Equal("SemanticOps", jo["module"]!.ToString());
            Assert.Equal("Transaction", jo["type"]!.ToString());
        }

        [Fact]
        public void SearchSource_ForwardsObjectNameAndResumeKnobs()
        {
            var args = new JObject
            {
                ["pattern"] = "foo",
                ["objectName"] = "A,B,C",
                ["startIndex"] = 100,
                ["timeoutMs"] = 5000
            };
            var routed = new SearchRouter().ConvertToolCall("genexus_search_source", args);
            Assert.NotNull(routed);
            var jo = JObject.FromObject(routed!);
            Assert.Equal("Search", jo["module"]!.ToString());
            Assert.Equal("A,B,C", jo["objectName"]!.ToString());
            Assert.Equal(100, jo["startIndex"]!.ToObject<int>());
            Assert.Equal(5000, jo["timeoutMs"]!.ToObject<int>());
        }

        [Fact]
        public void ReadInspectAndSearchForwardModularIdentity()
        {
            var read = JObject.FromObject(new ObjectRouter().ConvertToolCall(
                "genexus_read",
                JObject.Parse("{name:'ReverseOrder',type:'Procedure',guid:'g',entityKey:'ek',path:'Root Module/Operations/ReverseOrder'}"))!);
            Assert.Equal("g", read["guid"]!.ToString());
            Assert.Equal("ek", read["entityKey"]!.ToString());
            Assert.Equal("Root Module/Operations/ReverseOrder", read["path"]!.ToString());

            var inspect = JObject.FromObject(new AnalyzeRouter().ConvertToolCall(
                "genexus_inspect",
                JObject.Parse("{name:'ReverseOrder',type:'Procedure',guid:'g',entityKey:'ek',path:'Root Module/Operations/ReverseOrder'}"))!);
            Assert.Equal("g", inspect["guid"]!.ToString());
            Assert.Equal("ek", inspect["entityKey"]!.ToString());
            Assert.Equal("Root Module/Operations/ReverseOrder", inspect["path"]!.ToString());

            var search = JObject.FromObject(new SearchRouter().ConvertToolCall(
                "genexus_search_source",
                JObject.Parse("{pattern:'REVERSE',guid:'g',entityKey:'ek',path:'Root Module/Operations/ReverseOrder'}"))!);
            Assert.Equal("g", search["guid"]!.ToString());
            Assert.Equal("ek", search["entityKey"]!.ToString());
            Assert.Equal("Root Module/Operations/ReverseOrder", search["path"]!.ToString());
        }
    }
}
