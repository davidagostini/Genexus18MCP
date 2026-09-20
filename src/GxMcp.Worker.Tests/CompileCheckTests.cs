using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // mode=compile_check: spec+gen+compile the named object(s) plus their transitive
    // callers, routed through the targeted BuildOne path (skips the DeveloperMenu regen).
    // These tests exercise the synchronous "Accepted" envelope — the background build
    // itself needs a live KB, but caller expansion + the guard resolve up front.
    public class CompileCheckTests : BuildServiceTestBase
    {
        [Fact]
        public void CompileCheck_NoTarget_ReturnsNeedsTargetError()
        {
            var svc = new BuildService();
            var json = JObject.Parse(svc.CompileCheck(""));
            Assert.Equal("CompileCheckNeedsTarget", json["error"]?["code"]?.ToString());
        }

        [Fact]
        public void CompileCheck_ExpandsToTransitiveCallers()
        {
            // SmallCallGraph: A -> B -> C. compile_check on C must also build B and A
            // (the objects that would fail to compile if C's signature changed).
            var fx = TestFixtures.SmallCallGraph();
            var svc = new BuildService();
            svc.SetCallerGraphService(new CallerGraphService(fx.Index));

            var json = JObject.Parse(svc.CompileCheck("C"));

            Assert.Equal("Accepted", json["status"]?.ToString());
            var targets = json["targets"]?.Select(t => t.ToString()).ToList() ?? new System.Collections.Generic.List<string>();
            Assert.Contains("C", targets);
            Assert.Contains("B", targets);
            Assert.Contains("A", targets);

            var cc = json["compileCheck"];
            Assert.NotNull(cc);
            Assert.True(cc["callerGraphAvailable"]?.ToObject<bool>());
            var callersAdded = cc["callersAdded"]?.Select(t => t.ToString()).ToList() ?? new System.Collections.Generic.List<string>();
            Assert.Contains("A", callersAdded);
            Assert.Contains("B", callersAdded);
        }

        [Fact]
        public void CompileCheck_CallersFalse_ReportsCallerControls()
        {
            var fx = TestFixtures.SmallCallGraph();
            var svc = new BuildService();
            svc.SetIndexCacheService(fx.Index);
            svc.SetCallerGraphService(new CallerGraphService(fx.Index));

            var json = JObject.Parse(svc.CompileCheck("C", buildPlanCap: 200, includeCallers: false, callerCap: 1));
            var targets = json["targets"]?.Select(token => token.ToString()).ToArray();
            var compileCheck = json["compileCheck"];

            Assert.Equal("Accepted", json["status"]?.ToString());
            Assert.Equal(new[] { "C" }, targets);
            Assert.False(compileCheck?["callers"]?.ToObject<bool>());
            Assert.Equal(1, compileCheck?["callerCap"]?.ToObject<int>());
            Assert.Empty(compileCheck?["callersAdded"]?.ToObject<string[]>() ?? new string[0]);
            Assert.False(compileCheck?["truncated"]?.ToObject<bool>());
            Assert.Contains("caller expansion was disabled", compileCheck?["note"]?.ToString(),
                System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CompileCheck_NoCallerGraph_DegradesToNamedTargetsWithNote()
        {
            // No caller graph wired (index not built) → only the named object is checked,
            // and the envelope says so rather than silently implying full coverage.
            var svc = new BuildService();
            var json = JObject.Parse(svc.CompileCheck("Foo"));

            Assert.Equal("Accepted", json["status"]?.ToString());
            var targets = json["targets"]?.Select(t => t.ToString()).ToList() ?? new System.Collections.Generic.List<string>();
            Assert.Contains("Foo", targets);

            var cc = json["compileCheck"];
            Assert.NotNull(cc);
            Assert.False(cc["callerGraphAvailable"]?.ToObject<bool>());
            Assert.Contains("caller graph unavailable", cc["note"]?.ToString(),
                System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildDryRun_CompileCheck_CallersFalse_UsesTargetOnlyAndReportsControls()
        {
            var fx = TestFixtures.SmallCallGraph();
            var svc = new BuildService();
            svc.SetIndexCacheService(fx.Index);
            svc.SetCallerGraphService(new CallerGraphService(fx.Index));

            var json = JObject.Parse(svc.BuildDryRun(
                "CompileCheck", "C", "transitive", 200, includeCallers: false, callerCap: 1));
            var preview = json["result"]?["preview"];

            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("CompileCheck", preview?["action"]?.ToString());
            Assert.Equal("none", preview?["includeCallees"]?.ToString());
            Assert.False(preview?["callers"]?.ToObject<bool>());
            Assert.Equal(1, preview?["callerCap"]?.ToObject<int>());
            Assert.Equal(new[] { "C" }, preview?["wouldBuild"]?.ToObject<string[]>());
            Assert.Empty(preview?["callersAdded"]?.ToObject<string[]>() ?? new string[0]);
            Assert.False(preview?["truncated"]?.ToObject<bool>());
        }

        [Fact]
        public void BuildDryRun_CompileCheck_ExpandsCallersAndFlagsCallerCap()
        {
            var fx = TestFixtures.SmallCallGraph();
            var svc = new BuildService();
            svc.SetIndexCacheService(fx.Index);
            svc.SetCallerGraphService(new CallerGraphService(fx.Index));

            var json = JObject.Parse(svc.BuildDryRun(
                "CompileCheck", "C", "transitive", 200, includeCallers: true, callerCap: 1));
            var preview = json["result"]?["preview"];

            Assert.Equal(new[] { "C", "B" }, preview?["wouldBuild"]?.ToObject<string[]>());
            Assert.Equal(new[] { "B" }, preview?["callersAdded"]?.ToObject<string[]>());
            Assert.True(preview?["truncated"]?.ToObject<bool>());
            Assert.Equal(1, preview?["callerCap"]?.ToObject<int>());
            Assert.True(preview?["callerGraphAvailable"]?.ToObject<bool>());
        }

        [Fact]
        public void BuildDryRun_CompileCheck_RejectsAmbiguousBareTarget()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "B", Type = "Procedure" },
                new SearchIndex.IndexEntry { Name = "B", Type = "Transaction" }
            });
            var svc = new BuildService();
            svc.SetIndexCacheService(index);

            var json = JObject.Parse(svc.BuildDryRun("CompileCheck", "B", "transitive", 200));

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("BuildTargetAmbiguous", json["error"]?["code"]?.ToString());
            Assert.Contains("B", json["targets"]?.ToObject<string[]>() ?? new string[0]);
        }

        [Fact]
        public void BuildDryRun_CompileCheck_AcceptsTypeQualifiedTarget()
        {
            var fx = TestFixtures.SmallCallGraph();
            var svc = new BuildService();
            svc.SetIndexCacheService(fx.Index);

            var json = JObject.Parse(svc.BuildDryRun(
                "CompileCheck", "Procedure:C", "transitive", 200, includeCallers: false));

            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal(new[] { "Procedure:C" }, json["result"]?["preview"]?["wouldBuild"]?.ToObject<string[]>());
            Assert.True(json["result"]?["preview"]?["targetResolutionAvailable"]?.ToObject<bool>());
        }

        [Fact]
        public void BuildDryRun_CompileCheck_PutsTransactionCompanionBeforeTarget()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Order", Type = "Transaction" },
                new SearchIndex.IndexEntry { Name = "Order_bc", Type = "Procedure" }
            });
            Assert.Single(index.TryGetLoadedIndex().FindByType("Transaction"));
            Assert.Single(index.TryGetLoadedIndex().FindByName("Order_bc"));

            var svc = new BuildService();
            svc.SetIndexCacheService(index);

            var json = JObject.Parse(svc.BuildDryRun("CompileCheck", "Transaction:Order", "none", 200, includeCallers: false));
            var targets = json["result"]?["preview"]?["wouldBuild"]?.ToObject<string[]>();

            Assert.Equal(new[] { "Order_bc", "Transaction:Order" }, targets);
        }

        [Fact]
        public void BuildDryRun_CompileCheck_DoesNotClaimCallersForAmbiguousBareGraphSeed()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new List<SearchIndex.IndexEntry>
            {
                new SearchIndex.IndexEntry { Name = "Order", Type = "Transaction", CalledBy = new List<string> { "Caller" } },
                new SearchIndex.IndexEntry { Name = "Order", Type = "Table" },
                new SearchIndex.IndexEntry { Name = "Order_bc", Type = "Procedure" },
                new SearchIndex.IndexEntry { Name = "Caller", Type = "Procedure" }
            });
            var svc = new BuildService();
            svc.SetIndexCacheService(index);
            svc.SetCallerGraphService(new CallerGraphService(index));

            var json = JObject.Parse(svc.BuildDryRun("CompileCheck", "Transaction:Order", "none", 200));
            var preview = json["result"]?["preview"];

            Assert.Equal(new[] { "Order_bc", "Transaction:Order" }, preview?["wouldBuild"]?.ToObject<string[]>());
            Assert.False(preview?["callerGraphAvailable"]?.ToObject<bool>());
            Assert.Empty(preview?["callersAdded"]?.ToObject<string[]>() ?? new string[0]);
        }

        [Fact]
        public void BuildDryRun_CompileCheck_RejectsUnsupportedPathTarget()
        {
            var fx = TestFixtures.SmallCallGraph();
            var svc = new BuildService();
            svc.SetIndexCacheService(fx.Index);

            var json = JObject.Parse(svc.BuildDryRun(
                "CompileCheck", "Root Module/Folder/C", "transitive", 200));

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("CompileCheckTargetUnresolved", json["error"]?["code"]?.ToString());
            Assert.Contains("Type:Name", json["supportedTargetFormats"]?.ToString());
        }

        [Fact]
        public void CompilationPipeline_DryRun_PropagatesCompileCheckCallerControls()
        {
            var fx = TestFixtures.SmallCallGraph();
            var svc = new BuildService();
            svc.SetIndexCacheService(fx.Index);
            svc.SetCallerGraphService(new CallerGraphService(fx.Index));
            var pipeline = new CompilationPipeline(svc, (IEnvironmentManager)null);

            var json = JObject.Parse(pipeline.ExecuteBuild(
                "CompileCheck",
                "C",
                new JObject
                {
                    ["dryRun"] = true,
                    ["callers"] = false,
                    ["callerCap"] = 1
                }));

            Assert.Equal("CompileCheck", json["result"]?["preview"]?["action"]?.ToString());
            Assert.False(json["result"]?["preview"]?["callers"]?.ToObject<bool>());
            Assert.Equal(new[] { "C" }, json["result"]?["preview"]?["wouldBuild"]?.ToObject<string[]>());
        }
    }
}
