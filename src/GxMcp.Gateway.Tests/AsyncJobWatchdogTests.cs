using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Issue #79 — an async edit/variable/gxserver job waits on the SDK with no
    // timeout, so a blocked SDK call (IDE modal dialog holding the model, or the
    // SDK retrying a failing validation internally) left the job 'running' forever
    // with no actionable signal. The watchdog converts that dead end into a
    // terminal "stalled" state after a generous multiple of the caller's estimate.
    public class AsyncJobWatchdogTests
    {
        // Bound = max(10 min, min(est × 8, 60 min)).
        [Theory]
        [InlineData(30, 600)]      // default edit estimate → floor of 10 min
        [InlineData(120, 960)]     // gxserver default estimate → 8×
        [InlineData(360, 2880)]    // 8× inside the cap
        [InlineData(600, 3600)]    // capped at 60 min
        [InlineData(7200, 3600)]   // huge estimate still capped at 60 min
        [InlineData(1, 600)]       // tiny estimate floored at 10 min
        public void AsyncEditWatchdogMs_ComputesGenerousBound(int estimatedSeconds, int expectedSeconds)
        {
            int ms = Program.AsyncEditWatchdogMs(estimatedSeconds);
            Assert.Equal(expectedSeconds * 1000, ms);
        }

        [Fact]
        public void Stall_TransitionsRunningJobToStalled()
        {
            var reg = new BackgroundJobRegistry(600);
            var job = reg.Start("s1", "edit/genexus_edit", 30);
            reg.Stall(job.Id, "did not return within the 600s time bound", new JObject { ["status"] = "stalled" });

            var after = reg.Get(job.Id);
            Assert.NotNull(after);
            Assert.Equal("stalled", after!.Status);
            Assert.NotNull(after.CompletedAt);
            Assert.NotNull(after.Summary);
        }

        [Fact]
        public void Stall_UnknownJob_IsNoOp()
        {
            var reg = new BackgroundJobRegistry(600);
            reg.Stall("missing", "summary"); // must not throw
        }

        [Fact]
        public void Complete_AfterStall_DoesNotResurrectJob()
        {
            // A late worker response racing the watchdog must not flip a stalled job
            // back to succeeded/failed.
            var reg = new BackgroundJobRegistry(600);
            var job = reg.Start("s1", "edit/genexus_edit", 30);
            reg.Stall(job.Id, "stalled");
            reg.Complete(job.Id, success: true, summary: "Edit succeeded anyway");

            Assert.Equal("stalled", reg.Get(job.Id)!.Status);
        }

        [Fact]
        public void Cancel_AfterStall_ReturnsFalseAndKeepsStalled()
        {
            var reg = new BackgroundJobRegistry(600);
            var job = reg.Start("s1", "edit/genexus_edit", 30);
            reg.Stall(job.Id, "stalled");

            Assert.False(reg.Cancel(job.Id, "too late"));
            Assert.Equal("stalled", reg.Get(job.Id)!.Status);
        }

        [Fact]
        public void Cancel_AfterComplete_ReturnsFalseAndKeepsSucceeded()
        {
            // Terminal jobs are done — cancelling them must not rewrite history.
            var reg = new BackgroundJobRegistry(600);
            var job = reg.Start("s1", "edit/genexus_edit", 30);
            reg.Complete(job.Id, success: true, summary: "ok");

            Assert.False(reg.Cancel(job.Id, "too late"));
            Assert.Equal("succeeded", reg.Get(job.Id)!.Status);
        }

        [Fact]
        public void Complete_AfterComplete_SecondTerminalVerdictDoesNotClobberFirst()
        {
            // Only the first terminal verdict wins (poller vs reconcile race).
            var reg = new BackgroundJobRegistry(600);
            var job = reg.Start("s1", "lifecycle/build", 30);
            reg.Complete(job.Id, success: true, summary: "poller says ok");
            reg.Complete(job.Id, success: false, summary: "reconcile says failed");

            Assert.Equal("succeeded", reg.Get(job.Id)!.Status);
            Assert.Equal("poller says ok", reg.Get(job.Id)!.Summary);
        }

        [Fact]
        public void BuildJobResultEnvelope_Stalled_IsErrorWithActionableResult()
        {
            var reg = new BackgroundJobRegistry(600);
            var job = reg.Start("s1", "edit/genexus_edit", 30);
            reg.Stall(job.Id, "Edit did not return within the 600s time bound", Program.BuildStalledAsyncMutationEnvelope(job.Id, "genexus_edit", 30, 600));

            var (envelope, isErr) = McpRouter.BuildJobResultEnvelope(reg.Get(job.Id)!);

            Assert.True(isErr);
            Assert.Equal("stalled", envelope["status"]?.ToString());
            var result = envelope["result"] as JObject;
            Assert.NotNull(result);
            Assert.Equal("AsyncJobStalled", result!["code"]?.ToString());
            Assert.Equal("genexus_edit", result["tool"]?.ToString());
            Assert.NotNull(result["hint"]);
            Assert.Contains("async=true", result["hint"]!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildStalledAsyncMutationEnvelope_DisabledWatchdog_HasSaneBoundText()
        {
            var envelope = Program.BuildStalledAsyncMutationEnvelope("job-abc", "genexus_edit", 30, -1);
            Assert.Equal("stalled", envelope["status"]?.ToString());
            Assert.Equal(-1, envelope["boundSeconds"]?.ToObject<int>());
            Assert.Contains("watchdog disabled", envelope["message"]!.ToString());
            // The recovery hint must carry the real job id, not a placeholder.
            Assert.Contains("op:job-abc", envelope["hint"]!.ToString());
        }

        // Plan 069: when the watchdog recycles the wedged worker, the envelope must say
        // so — otherwise the agent sees only "stalled" and assumes the KB is still down.
        [Fact]
        public void BuildStalledAsyncMutationEnvelope_RecycledWorker_AddsRecoveryNote()
        {
            var envelope = Program.BuildStalledAsyncMutationEnvelope("job-abc", "genexus_edit", 30, 600, workerRecycled: true);
            Assert.Equal("stalled", envelope["status"]?.ToString());
            Assert.Equal(true, envelope["recycledWorker"]?.ToObject<bool>());
            Assert.NotNull(envelope["workerRecovery"]);
            Assert.Contains("force-recycled", envelope["workerRecovery"]!.ToString());
            // The recovery note must carry the real job id too.
            Assert.Contains("op:job-abc", envelope["hint"]!.ToString());
        }

        [Fact]
        public void BuildStalledAsyncMutationEnvelope_NoRecycle_OmitsRecoveryNote()
        {
            var envelope = Program.BuildStalledAsyncMutationEnvelope("job-abc", "genexus_edit", 30, 600);
            Assert.Null(envelope["recycledWorker"]);
            Assert.Null(envelope["workerRecovery"]);
        }
    }
}
