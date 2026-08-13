using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.6.6 Stream F: event-driven long-poll on action=status.
    // Replaces the gateway's 25s polling cap with a per-task ManualResetEventSlim
    // signalled by HandleLine when the baseline (Phase / counts / TargetsDone /
    // terminal Status) shifts. Tests register a synthetic BuildTaskStatus into
    // the static _tasks dictionary via reflection to avoid spinning up MSBuild.
    public class StatusWaitTests
    {
        private static (BuildService svc, BuildService.BuildTaskStatus status, string taskId) NewRunningTask()
        {
            var svc = new BuildService();
            var taskId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = taskId,
                Action = "Build",
                Target = "X",
                Status = "Running",
                Phase = "Starting",
                StartTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                StartedAt = DateTime.UtcNow
            };

            var fld = typeof(BuildService).GetField("_tasks", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(fld);
            var dict = (ConcurrentDictionary<string, BuildService.BuildTaskStatus>)fld!.GetValue(null)!;
            dict[taskId] = status;
            return (svc, status, taskId);
        }

        private static string BaselineFromJson(string json)
        {
            var jo = JObject.Parse(json);
            return jo["_meta"]?["snapshot"]?.ToString() ?? "";
        }

        [Fact]
        public void StatusWaitImmediateOnBaselineMismatch_Returns_Immediately()
        {
            var (svc, status, taskId) = NewRunningTask();

            // First, capture current baseline from a wait=0 call.
            string json0 = svc.GetStatusWait(taskId, waitSeconds: 0, sinceBaseline: null);
            string baseline = BaselineFromJson(json0);
            Assert.False(string.IsNullOrEmpty(baseline));

            // Mutate state under the lock so the next call sees a different baseline.
            lock (status._lock) { status.Phase = "Generating"; }

            var sw = Stopwatch.StartNew();
            string json1 = svc.GetStatusWait(taskId, waitSeconds: 10, sinceBaseline: baseline);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 500,
                $"Expected immediate return on baseline mismatch but took {sw.ElapsedMilliseconds} ms");
            string newBaseline = BaselineFromJson(json1);
            Assert.NotEqual(baseline, newBaseline);
        }

        [Fact]
        public async Task StatusWaitBlocksUntilPhaseChange()
        {
            var (svc, status, taskId) = NewRunningTask();
            string baseline;
            lock (status._lock) { baseline = status.ComputeBaseline(); }

            // Use reflection to call private HandleLine — it's the canonical
            // mutation path that fires the StateChangeSignal.
            var handleLine = typeof(BuildService).GetMethod("HandleLine", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(handleLine);

            var waitTask = Task.Run(() => svc.GetStatusWait(taskId, waitSeconds: 10, sinceBaseline: baseline));

            // Give the wait a moment to start blocking.
            await Task.Delay(100);

            var sw = Stopwatch.StartNew();
            // Feed a Generating line — HandleLine should flip Phase=Generating and Set the signal.
            handleLine!.Invoke(svc, new object[] { status, "Generating MyProc ...", false });

            var completedTask = await Task.WhenAny(waitTask, Task.Delay(2000));
            Assert.Same(waitTask, completedTask);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 1000,
                $"Wait took too long after phase change: {sw.ElapsedMilliseconds} ms");
            string waitResult = await waitTask;
            string newBaseline = BaselineFromJson(waitResult);
            Assert.NotEqual(baseline, newBaseline);
        }

        [Fact]
        public void StatusWaitTimeout_ReturnsAfterDeadline()
        {
            var (svc, status, taskId) = NewRunningTask();
            string baseline;
            lock (status._lock) { baseline = status.ComputeBaseline(); }

            var sw = Stopwatch.StartNew();
            string json = svc.GetStatusWait(taskId, waitSeconds: 1, sinceBaseline: baseline);
            sw.Stop();

            // Must wait close to the full second but not significantly overshoot.
            Assert.True(sw.ElapsedMilliseconds >= 900,
                $"Returned before timeout — {sw.ElapsedMilliseconds} ms");
            Assert.True(sw.ElapsedMilliseconds < 2500,
                $"Overshot timeout — {sw.ElapsedMilliseconds} ms");

            // Baseline unchanged (no state mutation occurred during the wait).
            string newBaseline = BaselineFromJson(json);
            Assert.Equal(baseline, newBaseline);
        }

        [Fact]
        public void StatusWaitTerminalSucceeded_ReturnsImmediatelyRegardlessOfSince()
        {
            var (svc, status, taskId) = NewRunningTask();
            lock (status._lock)
            {
                status.Status = "Succeeded";
                status.Phase = "Done";
            }

            var sw = Stopwatch.StartNew();
            // Pass a deliberately stale baseline equal to the current one to prove
            // the terminal short-circuit fires regardless of whether since matches.
            string baseline;
            lock (status._lock) { baseline = status.ComputeBaseline(); }
            string json = svc.GetStatusWait(taskId, waitSeconds: 30, sinceBaseline: baseline);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 200,
                $"Terminal task blocked for {sw.ElapsedMilliseconds} ms");
            var jo = JObject.Parse(json);
            Assert.Equal("Succeeded", jo["Status"]?.ToString());
        }

        [Fact]
        public void StatusWait_WaitZeroIsBackwardCompatible()
        {
            var (svc, status, taskId) = NewRunningTask();
            var sw = Stopwatch.StartNew();
            string json = svc.GetStatusWait(taskId, waitSeconds: 0, sinceBaseline: null);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 100, $"wait=0 blocked for {sw.ElapsedMilliseconds} ms");
            // Snapshot still surfaced for chaining.
            Assert.False(string.IsNullOrEmpty(BaselineFromJson(json)));
        }

        [Fact]
        public void StatusWait_UnknownTaskId_ReturnsImmediately()
        {
            var svc = new BuildService();
            var sw = Stopwatch.StartNew();
            string json = svc.GetStatusWait("does-not-exist", waitSeconds: 5, sinceBaseline: "anything");
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 200, $"Unknown taskId blocked for {sw.ElapsedMilliseconds} ms");
            Assert.Contains("Task ID not found", json);
        }
    }
}
