using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.3.8 (Task 2.2): exercises the new ListCriteria + ListService.List
    // overload with the LoadFromEntries fixture seam (no live KB required).
    public class ListDiscoveryTests
    {
        private static JArray ResultsOf(string json)
        {
            return (JArray)JObject.Parse(json)["results"];
        }

        [Fact]
        public void NameFilter_MatchesName_NotDescription()
        {
            var fixture = TestFixtures.IndexWithFolders();
            var svc = new ListService(fixture.Index);
            var json = svc.List(new ListCriteria { NameFilter = "Libera" });
            var hits = ResultsOf(json);
            Assert.Contains(hits, h => h["name"].ToString() == "ComissaoLiberaPareceres");
            Assert.DoesNotContain(hits, h => h["name"].ToString() == "PSPContParecer");
        }

        [Fact]
        public void DescriptionFilter_MatchesDescription_NotName()
        {
            var fixture = TestFixtures.IndexWithFolders();
            var svc = new ListService(fixture.Index);
            var json = svc.List(new ListCriteria { DescriptionFilter = "pareceres" });
            var hits = ResultsOf(json);
            Assert.Contains(hits, h => h["name"].ToString() == "PSPContParecer");
            // "ComissaoLiberaPareceres" has description "Liberar comissões" — no "pareceres" — so it must be excluded.
            Assert.DoesNotContain(hits, h => h["name"].ToString() == "ComissaoLiberaPareceres");
        }

        [Fact]
        public void PathPrefix_ListsFolderChildren()
        {
            var fixture = TestFixtures.IndexWithFolders();
            var svc = new ListService(fixture.Index);
            var json = svc.List(new ListCriteria { PathPrefix = "Root Module/ClickSign/" });
            var hits = ResultsOf(json);
            Assert.NotEmpty(hits);
            Assert.All(hits, h => Assert.StartsWith("Root Module/ClickSign/", h["parentFolderPath"].ToString()));
        }

        // Regression (empty-KB vs not-built): a fully-built index with 0 entries means the
        // KB genuinely has no model objects (e.g. a missing LocalDB model). list_objects must
        // return an honest empty listing tagged kb_has_no_objects instead of an eternal
        // IndexNotReady envelope — which previously left agents looping
        // `lifecycle action=index force=true` on empty KBs forever.
        [Fact]
        public void List_BuiltEmptyIndex_ReturnsEmptyListingWithKbHasNoObjects()
        {
            var idx = new IndexCacheService();
            // LoadFromEntries(markReady: true) is the in-memory equivalent of a completed
            // lite walk that found nothing → status Ready with 0 entries.
            idx.LoadFromEntries(new GxMcp.Worker.Models.SearchIndex.IndexEntry[0]);
            var svc = new ListService(idx);

            var obj = JObject.Parse(svc.List(new ListCriteria { Limit = 100 }));

            Assert.Equal(0, obj["total"].ToObject<int>());
            Assert.Empty((JArray)obj["results"]);
            Assert.Equal("kb_has_no_objects", obj["_meta"]["empty_reason"].ToString());
            Assert.NotNull(obj["_meta"]["emptyHint"]);
            // Must NOT be the IndexNotReady / Indexing envelope.
            Assert.Null(obj["code"]);
            Assert.NotEqual("Indexing", obj["status"]?.ToString());
        }

        // The gate must NOT have been loosened for genuinely-not-built indexes: a Cold
        // (never-loaded) index still fast-fails with IndexNotReady so the background build
        // can proceed instead of silently returning an empty page.
        [Fact]
        public void List_NotBuiltColdIndex_StillReturnsIndexNotReady()
        {
            var idx = new IndexCacheService(); // Cold by default — never loaded.
            var svc = new ListService(idx);

            var obj = JObject.Parse(svc.List(new ListCriteria { Limit = 100 }));

            Assert.Equal("IndexNotReady", obj["code"]?.ToString());
            Assert.Equal("Indexing", obj["status"]?.ToString());
        }

        [Fact]
        public void List_ExposesFreshnessWithoutChangingPagination()
        {
            ListService.InvalidateCache();
            var idx = new IndexCacheService();
            idx.LoadFromEntries(new[]
            {
                new GxMcp.Worker.Models.SearchIndex.IndexEntry { Name = "WarmProc", Type = "Procedure" }
            }, markReady: false);
            var captured = new System.DateTime(2026, 9, 3, 12, 34, 56, System.DateTimeKind.Utc);
            idx.MarkIndexLoaded(1, captured);

            var obj = JObject.Parse(new ListService(idx).List(new ListCriteria { Limit = 1 }));

            Assert.Equal(1, obj["pagination"]?["returned"]?.ToObject<int>());
            Assert.Equal("stale", obj["_meta"]?["freshness"]?["status"]?.ToString());
            Assert.Equal(captured, obj["_meta"]?["freshness"]?["lastSuccessfulScanAt"]?.ToObject<System.DateTime>().ToUniversalTime());
        }

        [Fact]
        public void List_UsesCache_WhenIndexUnchanged()
        {
            ListService.InvalidateCache();
            var fixture = TestFixtures.IndexWithFolders();
            var svc = new ListService(fixture.Index);

            var first = svc.List(new ListCriteria { Limit = 5 });
            var second = svc.List(new ListCriteria { Limit = 5 });

            Assert.Same(first, second); // identical string reference from cache

            ListService.InvalidateCache();
            var third = svc.List(new ListCriteria { Limit = 5 });
            Assert.Equal(first, third);
        }
    }
}
