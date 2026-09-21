using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WwpGridColumnEntryTests
    {
        [Theory]
        [InlineData("move_grid_column", true)]
        [InlineData("move_grid_column", false)]
        [InlineData("add_grid_variable", true)]
        [InlineData("add_grid_variable", false)]
        public void MissingVersionIsRejectedBeforeAnySdkAccess(string action, bool dryRun)
        {
            var service = new WwpActionService(null, null, null);
            var result = JObject.Parse(service.Run("Sample", new JObject { ["action"] = action, ["dryRun"] = dryRun }));
            Assert.Equal("ExpectedVersionRequired", (string)result["error"]?["code"] ?? (string)result["code"]);
        }
    }
}
