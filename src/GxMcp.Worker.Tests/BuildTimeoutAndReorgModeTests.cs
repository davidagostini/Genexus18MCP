using System;
using System.Collections.Generic;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #37 items 1-4: wall-clock build timeout resolution + reorg-mode
    // reporting in the reorg_preview envelope.
    public class BuildTimeoutAndReorgModeTests
    {
        [Fact]
        public void ResolveBuildTimeout_DefaultsAndFullKbBuildsAreLarger()
        {
            Environment.SetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC", null);
            Assert.Equal(900, BuildService.ResolveBuildTimeoutSeconds("Build"));
            Assert.Equal(2400, BuildService.ResolveBuildTimeoutSeconds("BuildAll"));
            Assert.Equal(2400, BuildService.ResolveBuildTimeoutSeconds("RebuildAll"));
        }

        [Fact]
        public void ResolveBuildTimeout_EnvOverrideWins_AndIsClamped()
        {
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC", "120");
                Assert.Equal(120, BuildService.ResolveBuildTimeoutSeconds("Build"));

                Environment.SetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC", "5");   // below floor
                Assert.Equal(60, BuildService.ResolveBuildTimeoutSeconds("Build"));

                Environment.SetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC", "99999"); // above ceiling
                Assert.Equal(7200, BuildService.ResolveBuildTimeoutSeconds("Build"));

                Environment.SetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC", "garbage"); // ignored → default
                Assert.Equal(900, BuildService.ResolveBuildTimeoutSeconds("Build"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_BUILD_TIMEOUT_SEC", null);
            }
        }

        [Fact]
        public void ReorgPreview_RequiresAnOpenKb()
        {
            var svc = new BuildService();
            var jo = JObject.Parse(svc.ReorgPreview("MyTrn"));
            Assert.Equal("error", jo["status"]?.ToString());
            Assert.Equal("NoKbOpen", jo["error"]?["code"]?.ToString());
        }

        [Fact]
        public void CheckReorgDisabled_ReturnsNull_WhenNoKb()
        {
            // No KbService set → cannot resolve mode → must not block reorg.
            var svc = new BuildService();
            Assert.Null(svc.CheckReorgDisabled());
        }

        // RunBuild phase extraction (YAGNI split): the factored Start*Timer methods
        // must preserve the inline behavior — disabled no-progress gate yields no
        // timer, enabled gates yield a timer that can be disposed before first due.
        [Fact]
        public void NoProgressWatchdog_NullWhenDisabled()
        {
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_BUILD_NOPROGRESS_SEC", "0");
                var status = new BuildService.BuildTaskStatus { TaskId = "t", Status = "Running", Phase = "Compiling" };
                Assert.Null(BuildService.StartNoProgressWatchdogTimer(status));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_BUILD_NOPROGRESS_SEC", null);
            }
        }

        [Fact]
        public void WatchdogTimers_CreatedAndDisposedBeforeFirstDue()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "t", Status = "Running", Phase = "Compiling" };
            var heartbeat = BuildService.StartBuildHeartbeatTimer(status);
            var wallClock = BuildService.StartWallClockWatchdogTimer(status, 900);
            try
            {
                Assert.NotNull(heartbeat);
                Assert.NotNull(wallClock);
            }
            finally
            {
                heartbeat?.Dispose();
                wallClock?.Dispose();
            }
        }

        [Fact]
        public void NoProgressWatchdog_CreatedWhenEnabled()
        {
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_BUILD_NOPROGRESS_SEC", "30");
                var status = new BuildService.BuildTaskStatus { TaskId = "t", Status = "Running", Phase = "Compiling" };
                var timer = BuildService.StartNoProgressWatchdogTimer(status);
                try
                {
                    Assert.NotNull(timer);
                }
                finally
                {
                    timer?.Dispose();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_BUILD_NOPROGRESS_SEC", null);
            }
        }

        // SnapshotPreBuildEvidence is best-effort by contract: against a scratch
        // dir (no KB, no generated .cs) it must not throw and must leave a
        // (possibly empty) mtime map for a code-emitting action.
        [Fact]
        public void SnapshotPreBuildEvidence_BestEffortAgainstScratchDir()
        {
            string kbPath = Path.Combine(Path.GetTempPath(), "GxTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(kbPath);
            try
            {
                var svc = new BuildService();
                var status = new BuildService.BuildTaskStatus { TaskId = "t", Status = "Running", SpecifyOnly = false };
                var ex = Record.Exception(() => svc.SnapshotPreBuildEvidence(status, "Build", new List<string> { "Foo" }, kbPath));
                Assert.Null(ex);
                Assert.NotNull(status.PreBuildMtimes);
            }
            finally
            {
                try { Directory.Delete(kbPath, true); } catch { }
            }
        }
    }
}
