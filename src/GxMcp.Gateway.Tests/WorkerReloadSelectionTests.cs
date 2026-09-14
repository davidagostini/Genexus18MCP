using System.Collections.Generic;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class WorkerReloadSelectionTests
    {
        private static IReadOnlyCollection<KbHandle> OpenKbs() => new[]
        {
            new KbHandle("orders", "C:/KB/Orders"),
            new KbHandle("warehouse", "C:/KB/Warehouse")
        };

        [Fact]
        public void Selected_session_kb_wins_over_pool_order()
        {
            var selected = new KbHandle("warehouse", "C:/KB/Warehouse");

            var result = Program.ResolveWorkerReloadTarget(null, selected, OpenKbs());

            Assert.NotNull(result);
            Assert.Equal("warehouse", result!.Alias);
        }

        [Fact]
        public void No_selection_does_not_guess_first_open_worker()
        {
            var result = Program.ResolveWorkerReloadTarget(null, null, OpenKbs());

            Assert.Null(result);
        }

        [Fact]
        public void Explicit_alias_can_select_an_open_worker()
        {
            var result = Program.ResolveWorkerReloadTarget("warehouse", null, OpenKbs());

            Assert.NotNull(result);
            Assert.Equal("warehouse", result!.Alias);
        }

        [Fact]
        public void Selected_kb_that_is_not_open_is_not_reloaded()
        {
            var selected = new KbHandle("missing", "C:/KB/Missing");

            var result = Program.ResolveWorkerReloadTarget(null, selected, OpenKbs());

            Assert.Null(result);
        }
    }
}
