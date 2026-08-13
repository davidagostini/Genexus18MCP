using Xunit;

namespace GxMcp.Worker.Tests
{
    // Friction 2026-05-28 — reapply projection beyond GENEXUS_MCP_REAPPLY_TIMEOUT_MS
    // (default 5 min) marks the response with code:"ProjectionTimedOut" +
    // recoveryRequired so the calling agent stops polling and reconnects.
    // STA SDK abort isn't safe, so the live path needs a real KB + IDE-tab
    // deadlock to exercise — convention test pins the contract here.
    public class ReapplyHardTimeoutTests
    {
        [Fact]
        public void PatternApplyService_HardTimeoutEnvelope_IsWired_ViaConvention()
        {
            string svcSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services",
                    "PatternApplyService.cs"));

            Assert.Contains("GENEXUS_MCP_REAPPLY_TIMEOUT_MS", svcSrc);
            Assert.Contains("ProjectionTimedOut", svcSrc);
            Assert.Contains("recoveryRequired", svcSrc);
            // Default cutoff is 5 minutes.
            Assert.Contains("hardTimeoutMs = 300_000", svcSrc);
            // Soft signal still present (slow-reapply hint).
            Assert.Contains("slowReapply", svcSrc);
        }
    }
}
