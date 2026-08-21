using System;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // issue #113 — a background build is not an in-flight RPC, so idle-reap / heap-recycle
    // decisions must also honor the worker's notifications/worker/build_active heartbeat
    // (emitted every 20s while a build runs). These tests drive ShouldStopForIdle /
    // ShouldRecycleForHeap through the same test-seam pattern as WorkerIdleTimeoutTests.
    public class WorkerBuildActiveGuardTests
    {
        private static WorkerProcess Make(int? idleMinutes = null, int heapRecycleMb = 0)
        {
            var config = new Configuration { Server = new ServerConfig() };
            if (idleMinutes.HasValue) config.Server.WorkerIdleTimeoutMinutes = idleMinutes.Value;
            config.Server.WorkerHeapRecycleMB = heapRecycleMb;
            return new WorkerProcess(config, new KbHandle("test", @"C:\fake\path"));
        }

        [Fact]
        public void RecentBuildActive_BlocksIdleReap_PastIdleWindow()
        {
            var worker = Make(idleMinutes: 2);
            // Activity is long stale — without the guard this would reap.
            worker.SetHeapProbeForTest(0, DateTime.UtcNow.AddMinutes(-30));
            worker.MarkBuildActiveForTest(DateTime.UtcNow.AddSeconds(-20)); // inside the 90s grace window
            Assert.False(worker.ShouldStopForIdleForTest());
        }

        [Fact]
        public void StaleBuildActive_DoesNotBlockIdleReap()
        {
            var worker = Make(idleMinutes: 2);
            worker.SetHeapProbeForTest(0, DateTime.UtcNow.AddMinutes(-30));
            worker.MarkBuildActiveForTest(DateTime.UtcNow.AddMinutes(-5)); // build ended long ago
            Assert.True(worker.ShouldStopForIdleForTest());
        }

        [Fact]
        public void NoBuildActiveSignal_IdleReapUnchanged()
        {
            var worker = Make(idleMinutes: 2);
            worker.SetHeapProbeForTest(0, DateTime.UtcNow.AddMinutes(-30));
            Assert.True(worker.ShouldStopForIdleForTest());
        }

        [Fact]
        public void RecentBuildActive_BlocksHeapRecycle()
        {
            var worker = Make(heapRecycleMb: 1); // 1 MB ceiling — anything over trips it
            worker.SetHeapProbeForTest(512 * 1024 * 1024, DateTime.UtcNow.AddMinutes(-30)); // 512 MB, idle past grace
            worker.MarkBuildActiveForTest(DateTime.UtcNow.AddSeconds(-10));
            Assert.False(worker.ShouldRecycleForHeap(out _));
        }

        [Fact]
        public void StaleBuildActive_HeapRecycleStillFires()
        {
            var worker = Make(heapRecycleMb: 1);
            worker.SetHeapProbeForTest(512 * 1024 * 1024, DateTime.UtcNow.AddMinutes(-30));
            worker.MarkBuildActiveForTest(DateTime.UtcNow.AddMinutes(-10));
            Assert.True(worker.ShouldRecycleForHeap(out _));
        }
    }
}
