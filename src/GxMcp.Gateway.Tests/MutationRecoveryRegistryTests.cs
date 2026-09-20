using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class MutationRecoveryRegistryTests
    {
        [Fact]
        public void TimedOutWrite_BlocksAnotherWriteUntilSamePartIsRead()
        {
            var registry = new MutationRecoveryRegistry();
            registry.RequireRead("kb-one", "SyntheticProcedure", "Source", "operation-one");

            Assert.True(registry.TryGet("KB-ONE", "syntheticprocedure", out var requirement));
            JObject blocked = MutationRecoveryRegistry.BuildBlockedEnvelope(requirement);
            Assert.Equal("error", blocked["status"]?.ToString());
            Assert.Equal("PostTimeoutReadRequired", blocked["error"]?["code"]?.ToString());
            Assert.False(blocked["error"]?["retryable"]?.ToObject<bool>());
            Assert.True(blocked["error"]?["reconciliationRequired"]?.ToObject<bool>());
            Assert.Equal("genexus_read", blocked["error"]?["nextSteps"]?[0]?["tool"]?.ToString());
            Assert.False(registry.ConfirmRead("kb-one", "SyntheticProcedure", "Rules"));
            Assert.True(registry.TryGet("kb-one", "SyntheticProcedure", out _));

            Assert.True(registry.ConfirmRead("kb-one", "SyntheticProcedure", "Source"));
            Assert.False(registry.TryGet("kb-one", "SyntheticProcedure", out _));
        }
    }
}
