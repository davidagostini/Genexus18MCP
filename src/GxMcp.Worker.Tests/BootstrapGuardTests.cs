using System.IO;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #88: UIServices.SetDisableUI(true) is mandatory before UI framework initialization
    // so modal dialogs don't wedge the worker's STA thread in headless environments.
    public class BootstrapGuardTests
    {
        [Fact]
        public void InitializeSdk_CallsSetDisableUI_BeforeUIServicesInitialize()
        {
            string progPath = Path.Combine(TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Program.cs");
            Assert.True(File.Exists(progPath), "Program.cs must exist at: " + progPath);

            string progSrc = File.ReadAllText(progPath);

            Assert.Contains("Step(\"UIServices.SetDisableUI\"", progSrc);
            Assert.Contains("t?.GetMethod(\"SetDisableUI\", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { true });", progSrc);

            int idxDisable = progSrc.IndexOf("Step(\"UIServices.SetDisableUI\"", System.StringComparison.Ordinal);
            int idxInit = progSrc.IndexOf("Step(\"UIServices.Initialize\"", System.StringComparison.Ordinal);

            Assert.True(idxDisable >= 0, "UIServices.SetDisableUI step must be present");
            Assert.True(idxInit >= 0, "UIServices.Initialize step must be present");
            Assert.True(idxDisable < idxInit, "UIServices.SetDisableUI must be called before UIServices.Initialize");
        }
    }
}
