using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class DryRunPlanTests
    {
        [Fact]
        public void BuildPlan_EmitsUnifiedDiff()
        {
            string before = "<A>1</A>";
            string after  = "<A>2</A>";

            var plan = DryRunPlanBuilder.Build("MyObject", before, after);

            Assert.NotNull(plan.XmlDiff);
            Assert.Contains("-<A>1</A>", plan.XmlDiff);
            Assert.Contains("+<A>2</A>", plan.XmlDiff);
            Assert.Single(plan.TouchedObjects);
            Assert.Equal("modify", plan.TouchedObjects[0].Op);
            Assert.Equal("MyObject", plan.TouchedObjects[0].Name);
        }

        [Fact]
        public void BuildPlan_DetectsRootTypeFromXml()
        {
            string xml = "<Procedure xmlns=\"urn:test\"><Source>msg(\"hi\")</Source></Procedure>";

            var plan = DryRunPlanBuilder.Build("TestProc", xml, xml);

            Assert.Equal("Procedure", plan.TouchedObjects[0].Type);
        }

        [Fact]
        public void PlanResponse_ToJson_UsesCamelCase()
        {
            var plan = new PlanResponse();
            plan.TouchedObjects.Add(new TouchedObject { Type = "Transaction", Name = "Cust", Op = "modify" });
            plan.XmlDiff = "--- before\n+++ after\n";
            plan.BrokenRefs.Add(new BrokenRef { From = "A", FromType = "Procedure", To = "B", Reason = "missing" });
            plan.Warnings.Add(new PlanWarning { Code = "W001", Message = "test", Path = "/x" });
            plan.EstimatedDurationMs = 42;

            var json = plan.ToJson();

            // All keys must be camelCase
            Assert.NotNull(json["touchedObjects"]);
            Assert.NotNull(json["xmlDiff"]);
            Assert.NotNull(json["brokenRefs"]);
            Assert.NotNull(json["warnings"]);
            Assert.NotNull(json["estimatedDurationMs"]);

            // Nested objects also camelCase
            var to = json["touchedObjects"][0] as JObject;
            Assert.NotNull(to["type"]);
            Assert.NotNull(to["name"]);
            Assert.NotNull(to["op"]);

            var br = json["brokenRefs"][0] as JObject;
            Assert.NotNull(br["from"]);
            Assert.NotNull(br["fromType"]);
            Assert.NotNull(br["to"]);
            Assert.NotNull(br["reason"]);
        }

        [Fact]
        public void BuildEnvelope_HasRequiredMetaFields()
        {
            string before = "<Object><Source>old</Source></Object>";
            string after  = "<Object><Source>new</Source></Object>";

            var env = DryRunPlanBuilder.BuildEnvelope("MyObj", before, after, "ops");

            Assert.False((bool)env["isError"]);
            Assert.True((bool)env["meta"]["dryRun"]);
            Assert.Equal("genexus_edit", (string)env["meta"]["tool"]);
            Assert.Equal("ops",          (string)env["meta"]["mode"]);
            Assert.Equal("mcp-axi/2",    (string)env["meta"]["schemaVersion"]);

            var planJson = env["plan"] as JObject;
            Assert.NotNull(planJson);
            Assert.NotNull(planJson["touchedObjects"]);

            string diff = (string)planJson["xmlDiff"];
            Assert.NotNull(diff);
            Assert.Contains("+", diff);
            Assert.Contains("-", diff);
        }

        [Fact]
        public void BuildEnvelope_WhenImpactAnalysisUnavailable_AddsWarning()
        {
            string before = "<Object><Source>old</Source></Object>";
            string after  = "<Object><Source>new</Source></Object>";

            var env = DryRunPlanBuilder.BuildEnvelope("MyObj", before, after, "ops");

            var warnings = (JArray)env["plan"]["warnings"];
            Assert.Contains(warnings, item =>
                item["code"]?.ToString() == "impactAnalysisUnavailable" &&
                item["path"]?.ToString() == "/plan/brokenRefs");
        }

        [Fact]
        public void ImpactAnalysis_ReportsExplicitMissingCallFromLoadedIndex()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "Caller", Type = "Procedure" },
                new SearchIndex.IndexEntry { Name = "Existing", Type = "Procedure" }
            });
            var validator = new KbValidationService(index, null, null);

            var refs = validator.AnalyzeImpact("Caller", "<Procedure><Source><![CDATA[call(MissingProc)\ncall(Existing)]]></Source></Procedure>");

            var missing = Assert.Single(refs);
            Assert.Equal("Caller", missing.From);
            Assert.Equal("Procedure", missing.FromType);
            Assert.Equal("MissingProc", missing.To);
            Assert.Contains("not found", missing.Reason);
        }

        [Fact]
        public void ImpactAnalysis_AcceptsModuleQualifiedKnownObject()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "Existing", Type = "Procedure" }
            });
            var validator = new KbValidationService(index, null, null);

            var refs = validator.AnalyzeImpact("Caller", "<Procedure><Source><![CDATA[udp(MyModule.Existing)]]></Source></Procedure>");

            Assert.Empty(refs);
        }

        [Fact]
        public void BuildEnvelope_WithValidatorButNoLoadedIndexAddsWarning()
        {
            var validator = new KbValidationService(new IndexCacheService(), null, null);

            var env = DryRunPlanBuilder.BuildEnvelope(
                "MyObj",
                "<Procedure><Source>old</Source></Procedure>",
                "<Procedure><Source>call(Missing)</Source></Procedure>",
                "patch",
                validator);

            var warnings = (JArray)env["plan"]["warnings"];
            Assert.Contains(warnings, item =>
                item["code"]?.ToString() == "impactAnalysisUnavailable");
            Assert.Empty((JArray)env["plan"]["brokenRefs"]);
        }
    }
}
