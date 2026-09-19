using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using System.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ModularBusinessComponentVariableRouterTests
    {
        [Fact]
        public void VariableAdd_ForwardsNativeBusinessComponentIdentityAndConcurrency()
        {
            var message = new OperationsRouter().ConvertToolCall("genexus_variable", JObject.Parse(@"{
                'action':'add',
                'name':'OrderAverageUpdate',
                'varName':'OrderRecord',
                'objectType':'BusinessComponent',
                'objectName':'OrderRecord',
                'module':'Operations',
                'expectedVersion':'version-1',
                'dryRun':true,
                'rollbackOnFailure':true
            }"));

            var routed = JObject.FromObject(message!);
            Assert.Equal("Write", routed["module"]?.ToString());
            Assert.Equal("AddVariable", routed["action"]?.ToString());
            Assert.Equal("BusinessComponent", routed["objectType"]?.ToString());
            Assert.Equal("OrderRecord", routed["objectName"]?.ToString());
            Assert.Equal("Operations", routed["objectModule"]?.ToString());
            Assert.Equal("version-1", routed["expectedVersion"]?.ToString());
            Assert.True(routed["dryRun"]?.ToObject<bool>());
            Assert.True(routed["rollbackOnFailure"]?.ToObject<bool>());
            Assert.Equal(JTokenType.Null, routed["validationMode"]?.Type);
        }

        [Fact]
        public void StructureUpdate_ForwardsAtomicDryRunAndExpectedVersion()
        {
            var message = new OperationsRouter().ConvertToolCall("genexus_structure", JObject.Parse(@"{
                'action':'update_visual',
                'name':'OrderRecord',
                'module':'Operations',
                'payload':{'children':[{'name':'OrderId'}]},
                'expectedVersion':'version-2',
                'dryRun':true
            }"));

            var routed = JObject.FromObject(message!);
            Assert.Equal("Structure", routed["module"]?.ToString());
            Assert.Equal("UpdateVisualStructure", routed["action"]?.ToString());
            Assert.Equal("Operations", routed["transactionModule"]?.ToString());
            Assert.Equal("version-2", routed["expectedVersion"]?.ToString());
            Assert.True(routed["dryRun"]?.ToObject<bool>());
            Assert.True(routed["rollbackOnFailure"]?.ToObject<bool>());
            Assert.Equal(JTokenType.Null, routed["validationMode"]?.Type);
        }

        [Fact]
        public void SchemasExposeTypedBusinessComponentAndAtomicStructureFields()
        {
            var definitions = JArray.Parse(System.IO.File.ReadAllText(FindToolDefinitions()));
            var variable = definitions.First(x => x["name"]?.ToString() == "genexus_variable");
            var structure = definitions.First(x => x["name"]?.ToString() == "genexus_structure");

            Assert.Equal("BusinessComponent",
                variable["inputSchema"]?["properties"]?["objectType"]?["enum"]?[0]?.ToString());
            Assert.NotNull(variable["inputSchema"]?["properties"]?["objectName"]);
            Assert.NotNull(variable["inputSchema"]?["properties"]?["module"]);
            Assert.NotNull(variable["inputSchema"]?["properties"]?["expectedVersion"]);
            Assert.NotNull(structure["inputSchema"]?["properties"]?["expectedVersion"]);
        }

        private static string FindToolDefinitions()
        {
            var directory = System.AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = System.IO.Path.Combine(directory, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (System.IO.File.Exists(candidate)) return candidate;
                var parent = System.IO.Directory.GetParent(directory);
                if (parent == null) break;
                directory = parent.FullName;
            }
            throw new System.IO.FileNotFoundException("tool_definitions.json was not found.");
        }
    }
}
