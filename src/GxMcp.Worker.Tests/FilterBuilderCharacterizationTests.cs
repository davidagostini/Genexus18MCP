using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Plan 006 Step 1: pins the CURRENT behavior of SearchService.Search and
    // ListService.ListObjects for the filter concepts a shared IndexEntryFilterBuilder
    // was factored out of (description substring, date range) plus the one concept
    // that deliberately was NOT merged (type matching — alias-aware in Search,
    // exact-set in List). These must keep passing unchanged after the refactor.
    public class FilterBuilderCharacterizationTests
    {
        private static List<SearchIndex.IndexEntry> SampleEntries()
        {
            var now = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
            return new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Proc1", Type = "Procedure", Description = "Handles ORDER processing", Guid = "g1", LastUpdate = now.AddDays(-5) },
                new SearchIndex.IndexEntry { Name = "WebForm1", Type = "WebForm", Description = "main screen", Guid = "g2", LastUpdate = now.AddDays(-2) },
                new SearchIndex.IndexEntry { Name = "WebFormAttr1", Type = "WebFormAttribute", Description = "an attribute on a webform", Guid = "g3", LastUpdate = now.AddDays(-1) },
                new SearchIndex.IndexEntry { Name = "Untouched", Type = "Table", Description = "no timestamp recorded", Guid = "g4", LastUpdate = DateTime.MinValue },
            };
        }

        private static IndexCacheService FullScanCache()
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(SampleEntries());
            return svc;
        }

        private static string[] SearchNames(string query, string typeFilter = null, DateTime since = default, DateTime modifiedBefore = default)
        {
            var svc = new SearchService(FullScanCache());
            var json = svc.Search(query, typeFilter, null, limit: 50, since: since, modifiedBefore: modifiedBefore);
            var results = (JArray)JObject.Parse(json)["results"];
            return results.Select(r => r["name"].ToString()).ToArray();
        }

        private static string[] ListNames(string typeFilter = null, string descriptionFilter = null, DateTime since = default, DateTime modifiedBefore = default)
        {
            var svc = new ListService(FullScanCache());
            var json = svc.List(new ListCriteria { TypeFilter = typeFilter, DescriptionFilter = descriptionFilter, Limit = 50, Since = since, ModifiedBefore = modifiedBefore });
            var results = (JArray)JObject.Parse(json)["results"];
            return results.Select(r => r["name"].ToString()).ToArray();
        }

        // ── DIVERGENCE (documented, deliberately preserved — see IndexEntryFilterBuilder.cs) ──

        [Fact]
        public void Search_TypeFilter_IsAliasAware_ExactSubstringOfAnotherTypeStillMatches()
        {
            // "WebForm" is a substring of "WebFormAttribute" too — IsTypeMatch's fallback
            // is `type.Contains(query)`, so Search's typeFilter="WebForm" over-matches.
            var names = SearchNames(query: "", typeFilter: "WebForm");
            Assert.Contains("WebForm1", names);
            Assert.Contains("WebFormAttr1", names); // over-match: alias-aware fallback is substring-based
        }

        [Fact]
        public void List_TypeFilter_IsExactSet_DoesNotOverMatchSubstringTypes()
        {
            // Same input, ListService's filterTypes.Contains(e.Type) requires an EXACT
            // (case-insensitive) type-name match — no substring over-match.
            var names = ListNames(typeFilter: "WebForm");
            Assert.Contains("WebForm1", names);
            Assert.DoesNotContain("WebFormAttr1", names);
        }

        [Fact]
        public void Search_TypeAlias_PrcMatchesProcedure()
        {
            var names = SearchNames(query: "", typeFilter: "prc");
            Assert.Equal(new[] { "Proc1" }, names);
        }

        [Fact]
        public void List_TypeFilter_RawAliasDoesNotMatch_NoTypeNamedPrc()
        {
            // List has no alias table: typeFilter="prc" looks for a type literally named
            // "prc", which doesn't exist in the sample set, so it matches nothing.
            var names = ListNames(typeFilter: "prc");
            Assert.Empty(names);
        }

        // ── SHARED BEHAVIOR (now routed through IndexEntryFilterBuilder in both services) ──

        [Fact]
        public void Search_DescriptionFilter_IsCaseInsensitiveSubstring()
        {
            var names = SearchNames(query: "description:order");
            Assert.Equal(new[] { "Proc1" }, names);
        }

        [Fact]
        public void List_DescriptionFilter_IsCaseInsensitiveSubstring()
        {
            var names = ListNames(descriptionFilter: "ORDER");
            Assert.Equal(new[] { "Proc1" }, names);
        }

        [Fact]
        public void Search_Since_IsInclusive_ModifiedBefore_IsExclusive()
        {
            var now = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
            // since = exactly Proc1's LastUpdate -> inclusive, Proc1 stays in.
            var names = SearchNames(query: "", since: now.AddDays(-5));
            Assert.Contains("Proc1", names);

            // modifiedBefore = exactly WebForm1's LastUpdate -> exclusive, WebForm1 drops out.
            var names2 = SearchNames(query: "", modifiedBefore: now.AddDays(-2));
            Assert.DoesNotContain("WebForm1", names2);
            Assert.Contains("Proc1", names2);
        }

        [Fact]
        public void List_Since_IsInclusive_ModifiedBefore_IsExclusive()
        {
            var now = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
            var names = ListNames(since: now.AddDays(-5));
            Assert.Contains("Proc1", names);

            var names2 = ListNames(modifiedBefore: now.AddDays(-2));
            Assert.DoesNotContain("WebForm1", names2);
            Assert.Contains("Proc1", names2);
        }

        [Fact]
        public void List_DateRange_ExcludesEntriesWithUnknownTimestamp()
        {
            var names = ListNames(modifiedBefore: new DateTime(2030, 1, 1));
            Assert.DoesNotContain("Untouched", names); // LastUpdate == DateTime.MinValue never matches ModifiedBefore
        }

        [Fact]
        public void Search_DateRange_ExcludesEntriesWithUnknownTimestamp()
        {
            var names = SearchNames(query: "", modifiedBefore: new DateTime(2030, 1, 1));
            Assert.DoesNotContain("Untouched", names);
        }

        [Fact]
        public void Search_WildcardQuery_MatchesAllObjects()
        {
            var names = SearchNames(query: "*");
            Assert.Contains("Proc1", names);
            Assert.Contains("WebForm1", names);
            Assert.Contains("WebFormAttr1", names);
        }
    }
}
