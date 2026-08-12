using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class StructureMoveAttributeRouterTests
    {
        [Fact]
        public void MoveAttribute_ForwardsNativeIdentityAndSafetyArguments()
        {
            var args = JObject.Parse(@"{
              'action':'move_attribute',
              'name':'SampleTransaction',
              'module':'SampleModule',
              'attribute':'SampleSubtypeId',
              'after':'SampleReferenceId',
              'levelPath':['Item','Operation'],
              'dryRun':true,
              'baseVersion':'12345'
            }");

            var routed = new OperationsRouter().ConvertToolCall("genexus_structure", args);
            var json = JObject.FromObject(routed!);

            Assert.Equal("Structure", json["module"]!.ToString());
            Assert.Equal("MoveAttribute", json["action"]!.ToString());
            Assert.Equal("SampleTransaction", json["target"]!.ToString());
            Assert.Equal("SampleModule", json["transactionModule"]!.ToString());
            Assert.Equal("SampleSubtypeId", json["attribute"]!.ToString());
            Assert.Equal("SampleReferenceId", json["after"]!.ToString());
            Assert.Equal("Operation", json["levelPath"]![1]!.ToString());
            Assert.True(json["dryRun"]!.Value<bool>());
            Assert.Equal("12345", json["baseVersion"]!.ToString());
        }

        [Fact]
        public void MoveAttribute_ForwardsZeroBasedPositionIncludingZero()
        {
            var args = JObject.Parse("{'action':'move_attribute','name':'T','attribute':'A','position':0}");
            var json = JObject.FromObject(new OperationsRouter().ConvertToolCall("genexus_structure", args)!);
            Assert.Equal(0, json["position"]!.Value<int>());
        }
    }
}
