using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Plan 046 spike: proves the ToolIdentity prototype can answer the key
    /// registry queries against the real tool_definitions.json + the real
    /// McpRouter.TryRewriteLegacyTool. Not wired into any production catalog.
    /// </summary>
    public class ToolIdentityTests
    {
        [Fact]
        public void ResolveCanonical_LegacyCreateObject_ResolvesToUmbrella()
        {
            Assert.Equal("genexus_create", ToolIdentity.ResolveCanonical("genexus_create_object"));
        }

        [Fact]
        public void ActionsFor_GenexusCreate_ContainsExpectedActions()
        {
            var actions = ToolIdentity.ActionsFor("genexus_create");
            Assert.Contains("object", actions);
            Assert.Contains("popup", actions);
            Assert.Contains("save_as", actions);
        }

        [Fact]
        public void IsKnownTool_CanonicalAndLegacyAlias_BothTrue()
        {
            Assert.True(ToolIdentity.IsKnownTool("genexus_create"));
            Assert.True(ToolIdentity.IsKnownTool("genexus_create_object"));
        }
    }
}
