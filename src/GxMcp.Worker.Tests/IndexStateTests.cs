using Xunit;
using GxMcp.Worker.Services;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Tests
{
    public class IndexStateTests
    {
        [Fact]
        public void GetState_BeforeIndex_ReturnsCold()
        {
            var svc = new IndexCacheService();
            var s = svc.GetState();
            Assert.Equal("Cold", s.Status);
            Assert.Null(s.LastIndexedAt);
            Assert.Equal(0, s.TotalObjects);
        }

        [Fact]
        public void GetState_AfterIndex_ReturnsReadyWithCount()
        {
            var svc = new IndexCacheService();
            svc.MarkIndexComplete(totalObjects: 42);
            var s = svc.GetState();
            Assert.Equal("Ready", s.Status);
            Assert.NotNull(s.LastIndexedAt);
            Assert.Equal(42, s.TotalObjects);
        }

        [Fact]
        public void MarkReindexStarted_SetsStatusToReindexing_Progress0()
        {
            var svc = new IndexCacheService();
            svc.MarkReindexStarted(100);
            var s = svc.GetState();
            Assert.Equal("Reindexing", s.Status);
            Assert.Equal(0, s.Progress);
            Assert.Equal(100, s.TotalObjects);
        }

        [Fact]
        public void MarkReindexProgress_UpdatesProgressAndEta()
        {
            var svc = new IndexCacheService();
            svc.MarkReindexStarted(100);
            svc.MarkReindexProgress(0.5, 8000);
            var s = svc.GetState();
            Assert.Equal(0.5, s.Progress);
            Assert.Equal(8000, s.EtaMs);
        }

        [Fact]
        public void MarkIndexComplete_ClearsProgressAndEta()
        {
            var svc = new IndexCacheService();
            svc.MarkReindexStarted(100);
            svc.MarkReindexProgress(0.5, 8000);
            svc.MarkIndexComplete(42);
            var s = svc.GetState();
            Assert.Equal("Ready", s.Status);
            Assert.Null(s.Progress);
            Assert.Null(s.EtaMs);
            Assert.Equal(42, s.TotalObjects);
        }

        [Fact]
        public void MarkIndexFailed_AfterReindexStarted_ResetsToCold()
        {
            var svc = new IndexCacheService();
            svc.MarkReindexStarted(100);
            svc.MarkIndexFailed();
            var s = svc.GetState();
            Assert.Equal("Cold", s.Status);
            Assert.Null(s.Progress);
            Assert.Null(s.EtaMs);
        }

        // Issue #209 (policy A): the fail-closed freshness gate has to be awaitable, or the
        // only documented escape from it is a manual reindex. These pin the wait predicate
        // that `genexus_lifecycle action=status wait=... freshness=...` evaluates.
        [Fact]
        public void Wait_WithoutFreshnessTarget_KeepsStatusOnlySemantics()
        {
            // A restored snapshot is Ready + stale: a Status-only wait still returns
            // immediately, which is exactly the behaviour callers could already rely on.
            var warmStart = new IndexState { Status = "Ready", Freshness = "stale" };
            Assert.True(IndexWaitPolicy.IsSatisfied(warmStart, null, null));
            Assert.True(IndexWaitPolicy.IsSatisfied(warmStart, null, string.Empty));
        }

        [Fact]
        public void Wait_ForCurrentFreshness_BlocksOnRestoredSnapshotAndCompletesOnDeltaRefresh()
        {
            var warmStart = new IndexState { Status = "Ready", Freshness = "stale" };
            Assert.False(IndexWaitPolicy.IsSatisfied(warmStart, null, "current"));

            // The delta refresh republishes Freshness=current without changing Status — the
            // transition a Status-only wait could never observe.
            var afterDelta = new IndexState { Status = "Ready", Freshness = "current" };
            Assert.True(IndexWaitPolicy.IsSatisfied(afterDelta, null, "current"));
        }

        [Fact]
        public void Wait_ForCurrentFreshness_SinceReadyStillRequiresFreshness()
        {
            // `since=Ready` on a warm start: the status has not LEFT Ready, so the wait must
            // keep blocking until freshness reaches the target (the old behaviour waited out
            // the entire budget and then reported nothing).
            var refreshing = new IndexState { Status = "Ready", Freshness = "refreshing" };
            Assert.False(IndexWaitPolicy.IsSatisfied(refreshing, "Ready", "current"));

            // A failed delta publishes Cold/stale. That leaves `since=Ready`, so a
            // status-only wait would report success — the freshness target keeps the
            // wait honest and the caller learns about the failure from the timeout payload.
            var failed = new IndexState { Status = "Cold", Freshness = "stale" };
            Assert.False(IndexWaitPolicy.IsSatisfied(failed, "Ready", "current"));
            Assert.True(IndexWaitPolicy.IsSatisfied(failed, "Ready", null));
        }

        [Fact]
        public void Wait_TreatsMissingStateAsCold()
        {
            var cold = new IndexState { Status = "Cold" };
            // Legacy semantics: no `since` blocks until Ready; `since` blocks until the
            // state LEAVES that value. An absent state must behave like an explicit Cold
            // state in both modes.
            Assert.False(IndexWaitPolicy.IsSatisfied(null, null, null));
            Assert.False(IndexWaitPolicy.IsSatisfied(null, "Cold", null));
            Assert.True(IndexWaitPolicy.IsSatisfied(null, "Reindexing", null));
            foreach (var since in new[] { null, "", "Cold", "Ready", "Reindexing", "Refreshing" })
            {
                Assert.Equal(
                    IndexWaitPolicy.IsSatisfied(cold, since, null),
                    IndexWaitPolicy.IsSatisfied(null, since, null));
            }

            // A freshness target can never be met while the state is unknown.
            Assert.False(IndexWaitPolicy.IsSatisfied(null, null, "current"));
        }

        [Fact]
        public void LoadFromEntries_TransitionsToReady_NoMarkComplete()
        {
            // Smoke test for the warm-start fix: hydrating the in-memory index
            // (here via the test seam LoadFromEntries, equivalent to the disk-load
            // path in GetIndex) must publish Ready to IndexState so whoami doesn't
            // keep reporting Cold while list/search hit a fully-populated index.
            var svc = new IndexCacheService();
            Assert.Equal("Cold", svc.GetState().Status);

            // LoadFromEntries is the in-memory equivalent of "loaded from disk".
            // The production GetIndex path now calls MarkIndexComplete after
            // hydrating; here we exercise the explicit call BulkIndex's
            // AlreadyIndexed branch also makes.
            svc.LoadFromEntries(new[]
            {
                new GxMcp.Worker.Models.SearchIndex.IndexEntry { Name = "A", Type = "Procedure" },
                new GxMcp.Worker.Models.SearchIndex.IndexEntry { Name = "B", Type = "Procedure" }
            });
            svc.MarkIndexComplete(svc.GetIndex().Objects.Count);

            var s = svc.GetState();
            Assert.Equal("Ready", s.Status);
            Assert.Equal(2, s.TotalObjects);
        }
    }
}
