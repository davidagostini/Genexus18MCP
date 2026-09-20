using System.Diagnostics;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class WorkerOwnershipRegistryTests
    {
        [Fact]
        public void OwnershipKey_IsolatedByWorkerExecutablePath()
        {
            string kb = @"C:\KBs\Shared";
            string checkoutA = WorkerOwnershipRegistry.BuildKey(@"C:\checkout-a\worker\GxMcp.Worker.exe", kb);
            string checkoutB = WorkerOwnershipRegistry.BuildKey(@"C:\checkout-b\worker\GxMcp.Worker.exe", kb);

            Assert.NotEqual(checkoutA, checkoutB);
            Assert.Equal(checkoutA, WorkerOwnershipRegistry.BuildKey(@"c:\CHECKOUT-A\worker\GxMcp.Worker.exe\", kb + "\\"));
        }

        [Fact]
        public void ProcessIdentity_RejectsPidReuseOrDifferentStartTime()
        {
            using (var process = Process.GetCurrentProcess())
            {
                var matching = new WorkerOwnershipRecord
                {
                    WorkerPid = process.Id,
                    WorkerStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks
                };
                var reused = new WorkerOwnershipRecord
                {
                    WorkerPid = process.Id,
                    WorkerStartTimeUtcTicks = matching.WorkerStartTimeUtcTicks + 1
                };

                Assert.True(WorkerOwnershipRegistry.IsSameProcess(process, matching));
                Assert.False(WorkerOwnershipRegistry.IsSameProcess(process, reused));
            }
        }
    }
}
