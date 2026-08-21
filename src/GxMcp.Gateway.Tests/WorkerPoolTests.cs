using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class WorkerPoolTests
    {
        private static Configuration CfgWithMax(int max) =>
            new Configuration { Server = new ServerConfig { MaxOpenKbs = max } };

        [Fact]
        public void ListOpen_excludes_entries_without_worker()
        {
            // ListOpen filters on Worker != null; with RegisterForTest the Worker is null,
            // so ListOpen returns empty (intentional — entries-in-flight aren't "open").
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            pool.RegisterForTest(new KbHandle("b", "C:/B"));
            var open = pool.ListOpen();
            Assert.Empty(open);
        }

        [Fact]
        public void SelectVictim_picks_oldest_lastActivity()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("a", "C:/A"), lastActivity: DateTime.UtcNow.AddMinutes(-10));
            pool.RegisterForTest(new KbHandle("b", "C:/B"), lastActivity: DateTime.UtcNow.AddMinutes(-1));
            var victim = pool.SelectVictimForTest();
            Assert.NotNull(victim);
            Assert.Equal("a", victim!.Alias);
        }

        [Fact]
        public void IsAtCapacity_respects_MaxOpenKbs()
        {
            // IsAtCapacity uses ">=", matching SpawnWorkerAsync's eviction threshold.
            // At-max (count == max) IS at capacity: opening one more KB would evict.
            var pool = new WorkerPool(CfgWithMax(2));
            Assert.False(pool.IsAtCapacity());
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            Assert.False(pool.IsAtCapacity());
            pool.RegisterForTest(new KbHandle("b", "C:/B"));
            Assert.True(pool.IsAtCapacity());  // exactly at max — at capacity
            pool.RegisterForTest(new KbHandle("c", "C:/C"));
            Assert.True(pool.IsAtCapacity());  // over max — at capacity
        }

        [Fact]
        public void Close_returns_true_when_present_false_when_absent()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            Assert.True(pool.Close("a"));
            Assert.False(pool.Close("a"));
            Assert.False(pool.Close("ghost"));
        }

        [Fact]
        public void Close_is_case_insensitive()
        {
            var pool = new WorkerPool(CfgWithMax(2));
            pool.RegisterForTest(new KbHandle("ProductionKb", "C:/P"));
            Assert.True(pool.Close("PRODUCTIONKB"));
        }

        // Drain regression: when an entry is draining, AcquireAsync must wait for
        // DrainComplete rather than returning the dying worker immediately.
        [Fact]
        public async Task AcquireAsync_during_drain_waits_for_DrainComplete()
        {
            var pool = new WorkerPool(CfgWithMax(5));
            var handle = new KbHandle("drainKb", "C:/Drain");
            // Register an entry with Worker=null so the fast path hits the draining check.
            pool.RegisterForTest(handle);
            // Mark it as draining.
            var drainTcs = pool.SetDrainingForTest("drainkb");
            Assert.True(pool.IsDrainingForTest("drainkb"));

            // AcquireAsync should be blocked on DrainComplete.
            var acquireTask = pool.AcquireAsync(handle, CancellationToken.None);
            // Give it a moment to start waiting.
            await Task.Delay(50);
            Assert.False(acquireTask.IsCompleted, "AcquireAsync should be blocked while draining.");

            // Signal drain complete; since Worker is still null, AcquireAsync will fall
            // through to the spawn path. With no real spawner wired up (test pool) it
            // will spawn a real WorkerProcess — we cancel instead to avoid that.
            using var cts = new CancellationTokenSource();
            var cancelledTask = pool.AcquireAsync(handle, cts.Token);
            drainTcs.TrySetResult(true);
            cts.Cancel();
            // After cancellation the task should fault/cancel, not return a worker.
            var ex = await Record.ExceptionAsync(() => cancelledTask);
            Assert.NotNull(ex);
        }

        // Drain regression: IsDrainingForTest reflects the draining flag accurately.
        [Fact]
        public void IsDrainingForTest_reflects_draining_state()
        {
            var pool = new WorkerPool(CfgWithMax(5));
            pool.RegisterForTest(new KbHandle("kb1", "C:/KB1"));
            Assert.False(pool.IsDrainingForTest("kb1"));
            pool.SetDrainingForTest("kb1");
            Assert.True(pool.IsDrainingForTest("kb1"));
        }

        // Plan 069: RecycleStalledWorker drops the live entry (like DropLiveEntry) but
        // stops the worker with the Wedged reason so the eager-respawn path fires; the
        // durable known set still survives so the KB stays resolvable.
        [Fact]
        public void RecycleStalledWorker_drops_live_entry_but_keeps_known()
        {
            var pool = new WorkerPool(CfgWithMax(3));
            pool.RegisterForTest(new KbHandle("adhoc", "C:/KB/AdHoc"));
            Assert.Contains(pool.ListKnown(), h => h.Alias == "adhoc");

            Assert.True(pool.RecycleStalledWorker("adhoc"));

            // Live entry gone (TryGet null), but still resolvable via the known set.
            Assert.Null(pool.TryGet("adhoc"));
            Assert.Contains(pool.ListKnown(), h => h.Alias == "adhoc");
        }

        [Fact]
        public void RecycleStalledWorker_absent_alias_returns_false()
        {
            var pool = new WorkerPool(CfgWithMax(3));
            Assert.False(pool.RecycleStalledWorker("ghost"));
            Assert.False(pool.RecycleStalledWorker(null!));
        }

        [Fact]
        public void RecycleStalledWorker_is_case_insensitive()
        {
            var pool = new WorkerPool(CfgWithMax(3));
            pool.RegisterForTest(new KbHandle("ProdKb", "C:/P"));
            Assert.True(pool.RecycleStalledWorker("PRODKB"));
        }

        // issue #26 P3: the durable known set survives a live-entry drop (worker recycle)
        // but is cleared by an explicit Close.
        [Fact]
        public void DropLiveEntry_keeps_known_but_removes_open()
        {
            var pool = new WorkerPool(CfgWithMax(3));
            pool.RegisterForTest(new KbHandle("adhoc", "C:/KB/AdHoc"));
            Assert.Contains(pool.ListKnown(), h => h.Alias == "adhoc");

            pool.DropLiveEntry("adhoc");

            // Live entry gone (TryGet null), but still resolvable via the known set.
            Assert.Null(pool.TryGet("adhoc"));
            Assert.Contains(pool.ListKnown(), h => h.Alias == "adhoc");
        }

        [Fact]
        public void Close_clears_known_registry()
        {
            var pool = new WorkerPool(CfgWithMax(3));
            pool.RegisterForTest(new KbHandle("adhoc", "C:/KB/AdHoc"));
            Assert.Contains(pool.ListKnown(), h => h.Alias == "adhoc");

            pool.Close("adhoc");

            Assert.DoesNotContain(pool.ListKnown(), h => h.Alias == "adhoc");
        }

        // Fix 9b (revised): IsAtCapacity and SpawnWorkerAsync share the same threshold.
        [Fact]
        public void IsAtCapacity_and_AcquireAsync_use_same_threshold()
        {
            // Both use count >= max. IsAtCapacity at exactly max is true;
            // consistent with SpawnWorkerAsync, which evicts when the other entries
            // alone already fill the cap (post-spawn total stays <= max).
            var pool = new WorkerPool(CfgWithMax(1));
            Assert.False(pool.IsAtCapacity()); // 0 entries, max=1 → 0 >= 1 is false
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            Assert.True(pool.IsAtCapacity());  // 1 entry, max=1 → 1 >= 1 is true
        }
    }
}
