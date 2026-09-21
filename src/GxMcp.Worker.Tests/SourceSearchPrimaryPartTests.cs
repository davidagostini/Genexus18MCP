using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SourceSearchPrimaryPartTests
    {
        [Theory]
        [InlineData("WebPanel", null)]
        [InlineData("Transaction", "source")]
        [InlineData("WebPanel", "code")]
        public void ColdAliasReadsEventsAndPromotesSamePart(string type, string scope)
        {
            var entry = Entry(type);
            var index = Index(entry);
            Seed(entry, "Source", "WrongPart()");
            Seed(entry, "Events", "EventOnlyNeedle()");
            var service = new SourceSearchService(index, new ObjectService(null, null));

            var result = Search(service, "EventOnlyNeedle", scope);

            Assert.Equal(1, result["count"].Value<int>());
            Assert.Equal("Events", result["hits"][0]["part"].Value<string>());
            Assert.Equal("EventOnlyNeedle()", entry.FullSource);
            var warm = Search(new SourceSearchService(index, null), "EventOnlyNeedle", scope);
            Assert.Equal(1, warm["count"].Value<int>());
            Assert.Equal("Events", warm["hits"][0]["part"].Value<string>());
        }

        [Theory]
        [InlineData("Procedure", "conditions")]
        [InlineData("WebPanel", "events")]
        [InlineData("WebPanel", "rules")]
        [InlineData("Transaction", "events")]
        [InlineData("Transaction", "rules")]
        public void ExplicitPartCannotBeDiscardedByUnrelatedIndexedSource(string type, string part)
        {
            var entry = Entry(type);
            entry.FullSource = "OtherPart()";
            var index = Index(entry);
            Seed(entry, part, "PartOnlyNeedle()");

            var result = Search(new SourceSearchService(index, new ObjectService(null, null)), "PartOnlyNeedle", part);

            Assert.Equal(1, result["count"].Value<int>());
            Assert.Equal(part, result["hits"][0]["part"].Value<string>());
        }

        [Theory]
        [InlineData("WebPanel")]
        [InlineData("Transaction")]
        public void GenericReadCannotPromoteWrongPartButEventsCan(string type)
        {
            var entry = Entry(type);
            var index = Index(entry);
            var service = new ObjectService(new KbService(index), null);
            var payload = new JObject { ["source"] = "Needle()" };

            Assert.False(service.TryPromoteCompleteSourceRead(Guid.Parse(entry.Guid), "Source", payload, null, "mcp", false));
            Assert.Null(entry.FullSource);
            Assert.True(service.TryPromoteCompleteSourceRead(Guid.Parse(entry.Guid), "Events", payload, null, "mcp", false));
            Assert.Equal("Needle()", entry.FullSource);
            Assert.Equal("Events", entry.FullSourcePart);
        }

        [Theory]
        [InlineData("WebPanel")]
        [InlineData("Transaction")]
        [InlineData("Procedure")]
        [InlineData("DataProvider")]
        public void OldDiskSourceIsDiscardedThenEventsPromotionSurvivesReload(string type)
        {
            var cache = new IndexCacheService();
            string kbPath = Path.Combine(Path.GetTempPath(), "search-parts-" + Guid.NewGuid().ToString("N"));
            cache.Initialize(kbPath, proactiveLoad: false);
            try
            {
                var entry = Entry(type);
                cache.ReplaceAll(new[] { entry });
                // Simulate a pre-fix shard: text without part provenance.
                entry.FullSource = "RulesOnlyNeedle()";
                Assert.True(cache.FlushNow());
                var restored = new IndexCacheService();
                restored.Initialize(kbPath, proactiveLoad: false);
                var loaded = restored.GetIndex().Objects.Values.Single();
                Assert.Null(loaded.FullSource);
                Assert.False(restored.GetIndex().SourceTokenIndex.ContainsKey("RulesOnlyNeedle"));
                string part = ObjectService.ResolveSearchPartName(type);
                Seed(loaded, part, "EventOnlyNeedle()");
                restored.MarkIndexComplete(1);
                var result = Search(new SourceSearchService(restored, new ObjectService(null, null)), "EventOnlyNeedle", null);
                Assert.Equal(1, result["count"].Value<int>());
                Assert.True(restored.FlushNow());
                var reloaded = new IndexCacheService();
                reloaded.Initialize(kbPath, proactiveLoad: false);
                Assert.Equal("EventOnlyNeedle()", reloaded.GetIndex().Objects.Values.Single().FullSource);
                Assert.Equal(part, reloaded.GetIndex().Objects.Values.Single().FullSourcePart);
                Assert.True(reloaded.GetIndex().SourceTokenIndex.ContainsKey("EventOnlyNeedle"));
            }
            finally { cache.DeleteOnDiskSnapshot(); }
        }

        [Theory]
        [InlineData("Procedure", "Source")]
        [InlineData("DataProvider", "Source")]
        [InlineData("WebPanel", "Events")]
        [InlineData("Transaction", "Events")]
        public void IndexedRegexAndCalleeHitsReportPrimaryPart(string type, string part)
        {
            var entry = Entry(type);
            entry.FullSource = "Needle()";
            entry.FullSourcePart = part;
            var index = Index(entry);
            var service = new SourceSearchService(index, null);
            foreach (var pattern in new[] { "Needle", "^Needle" })
            {
                var result = Search(service, pattern, null);
                Assert.Equal(1, result["count"].Value<int>());
                Assert.Equal(part, result["hits"][0]["part"].Value<string>());
            }
            var calls = JObject.Parse(service.SearchAsJson(new SourceSearchCriteria
            {
                Callee = "Needle", TimeoutMs = 30000, MaxResults = 10
            }))["result"];
            Assert.Equal(1, calls["count"].Value<int>());
            Assert.Equal(part, calls["hits"][0]["part"].Value<string>());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MixedScopeReadsRulesWithoutOverwritingEvents(bool scoped)
        {
            var entry = Entry("WebPanel");
            entry.FullSource = "EventOnlyNeedle()";
            entry.FullSourcePart = "Events";
            var index = Index(entry);
            Seed(entry, "Events", entry.FullSource);
            Seed(entry, "Rules", "RuleOnlyNeedle()");
            var result = JObject.Parse(new SourceSearchService(index, new ObjectService(null, null))
                .SearchAsJson(new SourceSearchCriteria
                {
                    Pattern = "RuleOnlyNeedle", Scope = new List<string> { "source", "rules" },
                    ObjectName = scoped ? entry.Name : null, MaxResults = 10, TimeoutMs = 30000
                }))["result"];
            Assert.Equal(1, result["count"].Value<int>());
            Assert.Equal("rules", result["hits"][0]["part"].Value<string>());
            Assert.Equal("EventOnlyNeedle()", entry.FullSource);
        }

        private static SearchIndex.IndexEntry Entry(string type) => new SearchIndex.IndexEntry
        {
            Name = "SearchProbe", Type = type, Guid = Guid.NewGuid().ToString()
        };

        private static IndexCacheService Index(SearchIndex.IndexEntry entry)
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[] { entry });
            index.MarkIndexComplete(1);
            return index;
        }

        private static void Seed(SearchIndex.IndexEntry entry, string part, string source)
        {
            Assert.True(ObjectService.CacheRawSourceFromReadPayload(Guid.Parse(entry.Guid), part,
                new JObject { ["source"] = source }, null, "mcp", false));
        }

        private static JToken Search(SourceSearchService service, string pattern, string scope) =>
            JObject.Parse(service.SearchAsJson(new SourceSearchCriteria
            {
                Pattern = pattern, Scope = scope == null ? null : new List<string> { scope },
                MaxResults = 10, TimeoutMs = 30000
            }))["result"];
    }
}
