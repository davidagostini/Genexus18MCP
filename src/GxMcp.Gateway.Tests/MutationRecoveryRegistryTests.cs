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
            Assert.Equal("PostTimeoutReadRequired", blocked["code"]?.ToString());
            Assert.False(registry.ConfirmRead("kb-one", "SyntheticProcedure", "Rules"));
            Assert.True(registry.TryGet("kb-one", "SyntheticProcedure", out _));

            Assert.True(registry.ConfirmRead("kb-one", "SyntheticProcedure", "Source"));
            Assert.False(registry.TryGet("kb-one", "SyntheticProcedure", out _));
        }
    }
}
