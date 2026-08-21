using Xunit;

namespace GxMcp.Gateway.Tests
{
    // issue #113 — the async-build background poller used to loop until its 30-minute
    // hard cap when the worker process died mid-build, leaving wait_until_done callers
    // hanging until the MCP transport timed out. A short run of consecutive failed
    // status polls is tolerated (transient hiccups); past that the job aborts as failed.
    public class BuildStatusPollPolicyTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void FewFailures_Tolerated(int failures)
        {
            Assert.False(BuildStatusPollPolicy.ShouldAbort(failures));
        }

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(100)]
        public void MaxConsecutiveFailures_Aborts(int failures)
        {
            Assert.True(BuildStatusPollPolicy.ShouldAbort(failures));
        }

        [Fact]
        public void Threshold_IsThree()
        {
            // Pinned so a bump is a deliberate decision: each failure can burn up to a
            // 30s poll timeout before the next attempt.
            Assert.Equal(3, BuildStatusPollPolicy.MaxConsecutiveFailures);
        }
    }
}
