using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class TransactionAttributeRemovalRouterTests
    {
        [Fact]
        public void GenexusEdit_RemoveAttribute_ForwardsPersistenceSafetyArguments()
        {
            var args = JObject.Parse(@"{
              'name':'Invoice',
              'part':'Structure',
              'mode':'ops',
              'type':'Transaction',
              'ops':[{'op':'remove_attribute','args':{
                'name':'InvoiceLegacyCode',
                'levelPath':['Item','Operation']
              }}],
              'dryRun':true,
              'baseVersion':'638905012345678901',
              'rollbackOnFailure':true
            }");

            var routed = new ObjectRouter().ConvertToolCall("genexus_edit", args);
            var json = JObject.FromObject(routed!);

            Assert.Equal("SemanticOps", json["module"]!.ToString());
            Assert.Equal("Apply", json["action"]!.ToString());
            Assert.Equal("Invoice", json["target"]!.ToString());
            Assert.Equal("Structure", json["part"]!.ToString());
            Assert.Equal("remove_attribute", json["ops"]![0]!["op"]!.ToString());
            Assert.Equal("Operation", json["ops"]![0]!["args"]!["levelPath"]![1]!.ToString());
            Assert.True(json["dryRun"]!.Value<bool>());
            Assert.True(json["rollbackOnFailure"]!.Value<bool>());
            Assert.Equal("638905012345678901", json["baseVersion"]!.ToString());
        }

        [Fact]
        public void GenexusEdit_RemoveAttribute_DefaultsRollbackOnFailureToTrue()
        {
            var args = JObject.Parse(@"{
              'name':'Invoice','part':'Structure','mode':'ops',
              'ops':[{'op':'remove_attribute','name':'InvoiceLegacyCode'}]
            }");

            var json = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_edit", args)!);
            Assert.True(json["rollbackOnFailure"]!.Value<bool>());
        }
    }
}
