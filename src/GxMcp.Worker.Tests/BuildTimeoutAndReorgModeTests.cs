using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

        // _tasks retention: terminal entries past TTL/cap are evicted
        // (oldest-completed first); non-terminal and just-terminal (<60s) entries
        // are never touched; the FullOutput buffer is released while the envelope
        // keeps answering. Ids are GUID-scoped so parallel classes sharing the
        // static registry can't interfere with the assertions.
        [Fact]
        public void SweepBuildTasks_EvictsOldTerminalKeepsRunningAndRecent()
        {
            DateTime now = DateTime.UtcNow;
            string oldId = "sweep-old-" + Guid.NewGuid().ToString("N");
            string runId = "sweep-run-" + Guid.NewGuid().ToString("N");
            string newId = "sweep-new-" + Guid.NewGuid().ToString("N");
            try
            {
                BuildService.InjectBuildTaskForTest(oldId, new BuildService.BuildTaskStatus
                {
                    TaskId = oldId, Status = "Succeeded",
                    StartedAt = now.AddHours(-5), ElapsedSeconds = 3600,
                    FullOutput = new StringBuilder("old-output")
                });
                BuildService.InjectBuildTaskForTest(runId, new BuildService.BuildTaskStatus
                {
                    TaskId = runId, Status = "Running",
                    StartedAt = now.AddHours(-5),
                    FullOutput = new StringBuilder("running-output")
                });
                BuildService.InjectBuildTaskForTest(newId, new BuildService.BuildTaskStatus
                {
                    TaskId = newId, Status = "Succeeded",
                    StartedAt = now.AddMinutes(-10), ElapsedSeconds = 60,
                    FullOutput = new StringBuilder("new-output")
                });
                int evicted = BuildService.SweepBuildTasks(now, taskCap: 1000000, taskTtlMinutes: 60, fullOutputKeepMinutes: 1000000);
                Assert.True(evicted >= 1);
                Assert.False(BuildService.TryGetBuildTaskForTest(oldId, out _));
                Assert.True(BuildService.TryGetBuildTaskForTest(runId, out _));
                Assert.True(BuildService.TryGetBuildTaskForTest(newId, out _));
            }
            finally
            {
                BuildService.RemoveBuildTaskForTest(oldId);
                BuildService.RemoveBuildTaskForTest(runId);
                BuildService.RemoveBuildTaskForTest(newId);
            }
        }

        [Fact]
        public void SweepBuildTasks_CapEvictsOldestButSparesJustTerminal()
        {
            DateTime now = DateTime.UtcNow;
            string olderId = "sweep-older-" + Guid.NewGuid().ToString("N");
            string oldId = "sweep-old2-" + Guid.NewGuid().ToString("N");
            string freshId = "sweep-fresh-" + Guid.NewGuid().ToString("N");
            try
            {
                BuildService.InjectBuildTaskForTest(olderId, new BuildService.BuildTaskStatus
                {
                    TaskId = olderId, Status = "Failed",
                    StartedAt = now.AddHours(-4), ElapsedSeconds = 3600
                });
                BuildService.InjectBuildTaskForTest(oldId, new BuildService.BuildTaskStatus
                {
                    TaskId = oldId, Status = "Succeeded",
                    StartedAt = now.AddHours(-3), ElapsedSeconds = 3600
                });
                BuildService.InjectBuildTaskForTest(freshId, new BuildService.BuildTaskStatus
                {
                    TaskId = freshId, Status = "Succeeded",
                    StartedAt = now.AddMinutes(-2), ElapsedSeconds = 90
                });
                int evicted = BuildService.SweepBuildTasks(now, taskCap: 0, taskTtlMinutes: 1000000, fullOutputKeepMinutes: 1000000);
                Assert.True(evicted >= 2);
                Assert.False(BuildService.TryGetBuildTaskForTest(olderId, out _));
                Assert.False(BuildService.TryGetBuildTaskForTest(oldId, out _));
                Assert.True(BuildService.TryGetBuildTaskForTest(freshId, out _));
            }
            finally
            {
                BuildService.RemoveBuildTaskForTest(olderId);
                BuildService.RemoveBuildTaskForTest(oldId);
                BuildService.RemoveBuildTaskForTest(freshId);
            }
        }

        [Fact]
        public void SweepBuildTasks_ReleasesFullOutputButKeepsEnvelope()
        {
            DateTime now = DateTime.UtcNow;
            string id = "sweep-env-" + Guid.NewGuid().ToString("N");
            try
            {
                BuildService.InjectBuildTaskForTest(id, new BuildService.BuildTaskStatus
                {
                    TaskId = id, Status = "Failed",
                    StartedAt = now.AddHours(-3), ElapsedSeconds = 3600,
                    FullOutput = new StringBuilder("full-text"),
                    Errors = new List<string> { "e1" }
                });
                BuildService.SweepBuildTasks(now, taskCap: 1000000, taskTtlMinutes: 1000000, fullOutputKeepMinutes: 60);
                Assert.True(BuildService.TryGetBuildTaskForTest(id, out var kept));
                Assert.Equal(0, kept.FullOutput.Length);
                Assert.Equal(new List<string> { "e1" }, kept.Errors);
            }
            finally
            {
                BuildService.RemoveBuildTaskForTest(id);
            }
        }

        [Fact]
        public void BuildTaskRetentionResolves_DefaultsFloorsAndOverrides()
        {
            try
            {
                Environment.SetEnvironmentVariable("GXMCP_BUILD_TASK_CAP", null);
                Environment.SetEnvironmentVariable("GXMCP_BUILD_TASK_TTL_MIN", null);
                Environment.SetEnvironmentVariable("GXMCP_BUILD_FULLOUTPUT_KEEP_MIN", null);
                Assert.Equal(50, BuildService.ResolveBuildTaskCap());
                Assert.Equal(180, BuildService.ResolveBuildTaskTtlMinutes());
                Assert.Equal(15, BuildService.ResolveBuildFullOutputKeepMinutes());

                Environment.SetEnvironmentVariable("GXMCP_BUILD_TASK_CAP", "3"); // below floor
                Environment.SetEnvironmentVariable("GXMCP_BUILD_TASK_TTL_MIN", "5"); // below floor
                Assert.Equal(10, BuildService.ResolveBuildTaskCap());
                Assert.Equal(60, BuildService.ResolveBuildTaskTtlMinutes());

                Environment.SetEnvironmentVariable("GXMCP_BUILD_TASK_CAP", "100");
                Environment.SetEnvironmentVariable("GXMCP_BUILD_TASK_TTL_MIN", "240");
                Environment.SetEnvironmentVariable("GXMCP_BUILD_FULLOUTPUT_KEEP_MIN", "30");
                Assert.Equal(100, BuildService.ResolveBuildTaskCap());
                Assert.Equal(240, BuildService.ResolveBuildTaskTtlMinutes());
                Assert.Equal(30, BuildService.ResolveBuildFullOutputKeepMinutes());
            }
            finally
            {
                Environment.SetEnvironmentVariable("GXMCP_BUILD_TASK_CAP", null);
                Environment.SetEnvironmentVariable("GXMCP_BUILD_TASK_TTL_MIN", null);
                Environment.SetEnvironmentVariable("GXMCP_BUILD_FULLOUTPUT_KEEP_MIN", null);
            }
        }
    }
}
