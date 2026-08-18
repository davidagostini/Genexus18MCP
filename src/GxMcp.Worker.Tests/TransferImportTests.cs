using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class TransferImportTests
    {
        [Fact]
        public void SilentImportOptions_DefaultsToIncrementalThemeIntegration()
        {
            Assert.Equal("IncrementalIntegration", TransferService.ResolveImportThemeBehavior(new JObject()));
            Assert.Equal("UseFromExport", TransferService.ResolveImportClassConflicts(new JObject()));
        }
    }
}
