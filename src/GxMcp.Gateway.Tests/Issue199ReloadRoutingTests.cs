using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class Issue199ReloadRoutingTests
    {
        [Fact]
        public void Reload_without_selection_is_allowed_for_the_only_open_worker()
        {
            Assert.True(Program.CanReloadWithoutLeaseForTest(new JObject(), openKbCount: 1));
        }

        [Fact]
        public void Reload_requires_a_selector_when_multiple_workers_are_open()
        {
            Assert.False(Program.CanReloadWithoutLeaseForTest(new JObject(), openKbCount: 2));
            Assert.True(Program.CanReloadWithoutLeaseForTest(new JObject { ["alias"] = "secondary" }, openKbCount: 2));
            Assert.True(Program.CanReloadWithoutLeaseForTest(new JObject { ["kb"] = "secondary" }, openKbCount: 2));
        }

        [Fact]
        public void ForceHardReload_is_rejected_before_worker_shutdown()
        {
            Assert.True(Program.IsForceHardReloadUnsupported(new JObject
            {
                ["force"] = true,
                ["mode"] = "hard",
                ["sourceDir"] = "C:/worker"
            }));
            Assert.False(Program.IsForceHardReloadUnsupported(new JObject
            {
                ["force"] = true,
                ["mode"] = "soft"
            }));
        }
    }
}
