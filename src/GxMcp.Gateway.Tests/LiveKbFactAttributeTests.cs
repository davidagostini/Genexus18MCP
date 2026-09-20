using System;
using System.IO;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class LiveKbFactAttributeTests
    {
        [Fact]
        public void TeamDevelopment_fixture_requirement_is_a_discovery_skip()
        {
            const string kbVariable = "GXMCP_TEST_KB";
            const string pendingVariable = "GXMCP_TEAMDEV_PENDING_NAME";
            string? previousKb = Environment.GetEnvironmentVariable(kbVariable);
            string? previousPending = Environment.GetEnvironmentVariable(pendingVariable);
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-live-attribute-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempKb);
            try
            {
                Environment.SetEnvironmentVariable(kbVariable, tempKb);
                Environment.SetEnvironmentVariable(pendingVariable, null);
                var attribute = new LiveKbFactAttribute(requiresTeamDevelopmentFixture: true);
                Assert.Contains("GXMCP_TEAMDEV_PENDING_NAME", attribute.Skip);
            }
            finally
            {
                Environment.SetEnvironmentVariable(kbVariable, previousKb);
                Environment.SetEnvironmentVariable(pendingVariable, previousPending);
                try { Directory.Delete(tempKb, recursive: true); } catch { }
            }
        }
    }
}
