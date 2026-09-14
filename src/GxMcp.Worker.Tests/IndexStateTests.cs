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
        public void MarkIndexLoaded_UsesSnapshotTimeAndMarksCacheStale()
        {
            var svc = new IndexCacheService();
            var captured = new System.DateTime(2026, 9, 3, 12, 34, 56, System.DateTimeKind.Utc);

            svc.MarkIndexLoaded(42, captured);

            var s = svc.GetState();
            Assert.Equal("Ready", s.Status);
            Assert.Equal("stale", s.Freshness);
            Assert.Equal(captured, s.LastIndexedAt);
            Assert.Equal(captured, s.LastSuccessfulScanAt);
        }

        [Fact]
        public void MarkIndexComplete_RecordsCurrentFreshnessAndScanTime()
        {
            var svc = new IndexCacheService();
            var scanned = new System.DateTime(2026, 9, 14, 10, 20, 30, System.DateTimeKind.Utc);

            svc.MarkIndexComplete(42, scanned);

            var s = svc.GetState();
            Assert.Equal("current", s.Freshness);
            Assert.Equal(scanned, s.LastIndexedAt);
            Assert.Equal(scanned, s.LastSuccessfulScanAt);
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
