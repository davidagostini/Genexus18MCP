using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class IncomingReferenceRegressionTests
    {
        private static IndexCacheService Index()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[] {
                new SearchIndex.IndexEntry { Name = "Target", Type = "Procedure", IsEnriched = true },
                new SearchIndex.IndexEntry { Name = "Caller", Type = "WebPanel", IsEnriched = true },
                new SearchIndex.IndexEntry { Name = "Outer", Type = "Procedure", IsEnriched = true }
            });
            return index;
        }

        [Fact]
        public void ImpactUp_RecoversSdkEdgesAndOrdersDependenciesFirst()
        {
            var index = Index();
            var service = new GraphToolsService(null, null, index, new CallerGraphService(index));
            service.IncomingReferenceReader = name => name == "Target" ? new[] { "Caller" }
                : name == "Caller" ? new[] { "Outer" } : new string[0];
            var result = JObject.Parse(service.Impact(new JObject { ["name"] = "Target", ["direction"] = "up", ["maxDepth"] = 2 }))["result"];
            Assert.Equal(new[] { "Caller", "Outer" }, result["affected"].Select(x => (string)x["name"]));
            Assert.Equal(new[] { "Target", "Caller", "Outer" }, result["order"].Values<string>());
            Assert.Equal("complete", (string)result["fieldTrust"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ImpactUp_UnavailableOrFailedSdkNeverConfirmsEmptyIncomingSet(bool throws)
        {
            var index = Index();
            var service = new GraphToolsService(null, null, index, new CallerGraphService(index));
            service.IncomingReferenceReader = _ => throws ? throw new InvalidOperationException("synthetic failure") : null;
            var result = JObject.Parse(service.Impact(new JObject { ["name"] = "Target", ["direction"] = "up" }))["result"];
            Assert.Empty(result["affected"]);
            Assert.Equal("partial", (string)result["fieldTrust"]);
            Assert.Contains("not a confirmed", (string)result["coverageNote"]);
        }

        [Theory]
        [InlineData("up")]
        [InlineData("both")]
        public void ImpactSdkRecovery_RespectsDepthAndNodeBudgets(string direction)
        {
            var index = Index();
            var service = new GraphToolsService(null, null, index, new CallerGraphService(index));
            service.IncomingReferenceReader = name => name == "Target" ? new[] { "Caller", "Outer" }
                : name == "Caller" ? new[] { "More" } : new string[0];
            var result = JObject.Parse(service.Impact(new JObject { ["name"] = "Target", ["direction"] = direction,
                ["maxDepth"] = 1, ["maxNodes"] = 1 }))["result"];
            Assert.Single(result["affected"]);
            Assert.Equal("Caller", (string)result["affected"][0]["name"]);
            Assert.True((bool)result["stats"]["truncated"]);
            Assert.Contains("maxNodes", result["stats"]["truncatedBy"].Values<string>());
            Assert.Contains("maxDepth", result["stats"]["truncatedBy"].Values<string>());
        }

        [Fact]
        public void ImpactDown_DoesNotReadIncomingSdkReferences()
        {
            var index = Index();
            var service = new GraphToolsService(null, null, index, new CallerGraphService(index));
            int reads = 0;
            service.IncomingReferenceReader = _ => { reads++; return new string[0]; };
            var result = JObject.Parse(service.Impact(new JObject { ["name"] = "Target", ["direction"] = "down" }))["result"];
            Assert.Empty(result["affected"]);
            Assert.Equal("complete", (string)result["fieldTrust"]);
            Assert.Equal(0, reads);
        }

        [Fact]
        public void ImpactIncomingBudget_OverrunKeepsRecoveredDependentsButReportsPartial()
        {
            var index = Index();
            var service = new GraphToolsService(null, null, index, new CallerGraphService(index));
            int reads = 0;
            service.IncomingReferenceReader = _ => {
                reads++;
                System.Threading.Thread.Sleep(5100);
                return new[] { "Caller" };
            };
            var result = JObject.Parse(service.Impact(new JObject { ["name"] = "Target", ["direction"] = "up" }))["result"];
            Assert.Equal(1, reads);
            Assert.Equal("Caller", (string)result["affected"][0]["name"]);
            Assert.Equal("partial", (string)result["fieldTrust"]);
            Assert.NotEmpty((string)result["coverageNote"]);
        }

        [Fact]
        public void CallerScan_CanonicalEventsPreservesSameLineRulesOccurrence()
        {
            var index = Index();
            var service = new AnalyzeService(index, null, new CallerGraphService(index),
                (name, part) => name == "Caller" ? "Target.Call()" : null);
            var sites = service.ScanSdkCallerSources(new[] { "Caller" }, "Target", "Target");
            Assert.Equal(2, sites.Count);
            Assert.Equal(new[] { "Events", "Rules" }, sites.Select(x => (string)x["part"]));
            Assert.All(sites, x => Assert.Equal(1, (int)x["line"]));
        }

        [Fact]
        public void CanonicalParts_OnlyEventsAliasIsRemoved()
        {
            Assert.Equal(new[] { "Events", "Rules" }, AnalyzeService.CanonicalCallerParts(new[] { "source", "events", "rules", "Variables" }));
            Assert.Equal(new[] { "Source", "Rules" }, AnalyzeService.CanonicalCallerParts(new[] { "Source", "Rules" }));
            Assert.Equal(new[] { "SDEvents", "SDRules" }, AnalyzeService.CanonicalCallerParts(new[] { "Source", "Events", "Rules", "SDEvents", "SDRules" }));
        }

        [Fact]
        public void SdPanelCallerScansPreserveCanonicalEventsAndRulesInBothPaths()
        {
            var index = Index();
            index.GetIndex().FindByName("Caller").Single().Type = "SDPanel";
            index.GetIndex().FindByName("Target").Single().CalledBy = new List<string> { "Caller" };
            var reads = new List<string>();
            var service = new AnalyzeService(index, null, new CallerGraphService(index), (name, part) => {
                if (name != "Caller") return null;
                reads.Add(part);
                return part == "SDEvents" || part == "SDRules" ? "Target.Call()" : null;
            });
            var sdkSites = service.ScanSdkCallerSources(new[] { "Caller" }, "Target", "Target");
            Assert.Equal(new[] { "SDEvents", "SDRules" }, reads);
            Assert.Equal(new[] { "SDEvents", "SDRules" }, sdkSites.Select(site => (string)site["part"]));
            reads.Clear();
            var result = JObject.Parse(service.FindCallerSites("Target"))["result"];
            Assert.Equal(2, (int)result["callSiteCount"]);
            Assert.Equal(new[] { "SDEvents", "SDRules" }, reads);
            Assert.Equal(new[] { "SDEvents", "SDRules" }, result["callers"].Select(site => (string)site["part"]));
        }

        [Fact]
        public void FormulaDependents_AttributeFormulaIsTypedAndPreservedVerbatim()
        {
            const string formula = "Target(Value) IF Value > 0;";
            var dependent = AnalyzeService.DescribeFormulaDependent("ComputedValue", "Attribute", formula);
            Assert.Equal("ComputedValue", (string)dependent["attribute"]);
            Assert.Equal(formula, (string)dependent["formula"]);
            Assert.Null(AnalyzeService.DescribeFormulaDependent("Caller", "Procedure", formula));
            Assert.Null(AnalyzeService.DescribeFormulaDependent("PlainValue", "Attribute", ""));
            Assert.Null(SourceSearchService.ReadAttributeFormula(null));
        }

        [Fact]
        public void CallerSites_MixedIndexAndSdkFormulaKeepsBothKindsOfEvidence()
        {
            var index = Index();
            index.GetIndex().FindByName("Target").Single().CalledBy = new List<string> { "Caller" };
            var service = new AnalyzeService(index, null, new CallerGraphService(index),
                (name, part) => name == "Caller" && part == "Events" ? "Target.Call()" : null);
            service.SdkReferenceReader = _ => {
                var sdk = new AnalyzeService.SdkRefs();
                sdk.Callers.Add("ComputedValue");
                sdk.FormulaDependents.Add(AnalyzeService.DescribeFormulaDependent("ComputedValue", "Attribute", "Target(Value)"));
                return sdk;
            };
            var result = JObject.Parse(service.FindCallerSites("Target"))["result"];
            Assert.Equal(1, (int)result["callSiteCount"]);
            Assert.Equal("Caller", (string)result["callers"][0]["object"]);
            Assert.Single(result["formulaDependents"]);
            Assert.Equal("ComputedValue", (string)result["formulaDependents"][0]["attribute"]);
            Assert.Equal("Target(Value)", (string)result["formulaDependents"][0]["formula"]);
            Assert.Equal("complete", (string)result["formulaDependentsCoverage"]);
        }

        [Fact]
        public void CallerSites_SdkFormulaRetainsLegacyNamesAndAddsTypedEvidence()
        {
            var index = Index();
            var service = new AnalyzeService(index, null, new CallerGraphService(index), (_, __) => null);
            service.SdkReferenceReader = _ => {
                var sdk = new AnalyzeService.SdkRefs();
                sdk.Callers.Add("ComputedValue");
                sdk.CallerDetails.Add(new JObject { ["name"] = "ComputedValue", ["type"] = "Attribute" });
                sdk.FormulaDependents.Add(AnalyzeService.DescribeFormulaDependent("ComputedValue", "Attribute", "Target(Value)"));
                return sdk;
            };
            var result = JObject.Parse(service.FindCallerSites("Target"))["result"];
            Assert.Equal("ComputedValue", (string)result["sdkCallers"][0]);
            Assert.Equal("Attribute", (string)result["sdkCallerDetails"][0]["type"]);
            Assert.Equal("Target(Value)", (string)result["formulaDependents"][0]["formula"]);
            Assert.Equal(0, (int)result["callSiteCount"]);
        }

        [Fact]
        public void FormulaFieldSearch_UsesMetadataPipelineAndLeavesDefaultSourceSearchUntouched()
        {
            var index = Index();
            index.GetIndex().Objects["Attribute:ComputedValue"] = new SearchIndex.IndexEntry {
                Guid = Guid.NewGuid().ToString(), Name = "ComputedValue", Type = "Attribute", Description = "computed"
            };
            var service = new SourceSearchService(index, null);
            int reads = 0;
            service.AttributeFormulaReader = e => { reads++; Assert.Equal("Attribute", e.Type); return "Target(Value) IF Value > 0;"; };
            var result = JObject.Parse(service.SearchAsJson(new SourceSearchCriteria {
                Pattern = "target\\(", Fields = new List<string> { "formula" }, TimeoutMs = 30000
            }))["result"];
            var hits = result["hits"];
            Assert.Single(hits);
            Assert.Equal("ComputedValue", (string)hits[0]["objectName"]);
            Assert.Equal("formula", (string)hits[0]["field"]);
            Assert.Equal("Target(Value) IF Value > 0;", (string)hits[0]["matchedValue"]);
            Assert.Null(hits[0]["line"]);
            Assert.Equal(1, reads);
            reads = 0;
            service.SearchAsJson(new SourceSearchCriteria { Pattern = "Target", TimeoutMs = 30000 });
            Assert.Equal(0, reads);
        }
    }
}
