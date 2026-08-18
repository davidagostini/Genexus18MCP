using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class LifecycleAsyncBuildDispatchTests
    {
        [Fact]
        public void BuildCommandFactory_UsesRebuildAllForRebuild()
        {
            var command = Program.BuildAsyncLifecycleCommand(
                "rebuild",
                new JObject { ["target"] = "Customer" },
                "job-2");

            Assert.Equal("RebuildAll", command["action"]!.ToString());
            Assert.Equal("job-2", command["cancelToken"]!.ToString());
        }
    }
}
