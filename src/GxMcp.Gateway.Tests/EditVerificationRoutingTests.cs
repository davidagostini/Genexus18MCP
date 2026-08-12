using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class EditVerificationRoutingTests
    {
        [Fact]
        public void Patch_ForwardsVerificationConcurrencyAndExplicitRollback()
        {
            var args = new JObject
            {
                ["name"] = "SyntheticProcedure",
                ["type"] = "Procedure",
                ["part"] = "Source",
                ["mode"] = "patch",
                ["operation"] = "Replace",
                ["context"] = "old",
                ["content"] = "new",
                ["verifyMode"] = "normalized",
                ["baseVersion"] = "version-token",
                ["rollbackOnFailure"] = true
            };

            var routed = JObject.FromObject(new ObjectRouter().ConvertToolCall("genexus_edit", args));
            Assert.Equal("normalized", routed["verifyMode"]?.ToString());
            Assert.Equal("version-token", routed["baseVersion"]?.ToString());
            Assert.True(routed["rollbackOnFailure"]?.Value<bool>());
        }
    }
}
