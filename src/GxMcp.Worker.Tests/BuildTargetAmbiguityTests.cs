using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class BuildTargetAmbiguityTests
    {
        [Fact]
        public void ExpandTargetsFailsClosedWhenBareNameHasMultipleTypes()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "Shared", Type = "Procedure", Calls = new List<string>() },
                new SearchIndex.IndexEntry { Name = "Shared", Type = "WebPanel", Calls = new List<string>() }
            });
            index.MarkIndexComplete(2);
            var build = new BuildService();
            build.SetIndexCacheService(index);
            build.SetCallerGraphService(new CallerGraphService(index));

            var plan = build.ExpandTargets(new[] { "Shared" }, "none", 20);
            Assert.Contains("Shared", plan.AmbiguousTargets);
            Assert.Equal(new[] { "Shared" }, plan.Expanded);
        }

        [Fact]
        public void ExpandTargetsReportsUnresolvedTargetWhenIndexIsReady()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "Known", Type = "Procedure", Calls = new List<string>() }
            });
            index.MarkIndexComplete(1);
            var build = new BuildService();
            build.SetIndexCacheService(index);

            var plan = build.ExpandTargets(new[] { "Typo" }, "none", 20);
            Assert.True(plan.TargetResolutionAvailable);
            Assert.Contains("Typo", plan.UnresolvedTargets);
            Assert.Equal(new[] { "Typo" }, plan.Expanded);
        }

        [Fact]
        public void BuildDryRunRejectsUnresolvedTargetWhenIndexIsReady()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "Known", Type = "Procedure", Calls = new List<string>() }
            });
            index.MarkIndexComplete(1);
            var build = new BuildService();
            build.SetIndexCacheService(index);

            var json = Newtonsoft.Json.Linq.JObject.Parse(
                build.BuildDryRun("Build", "Typo", "none", 20));
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("BuildTargetUnresolved", json["error"]?["code"]?.ToString());
            Assert.True(json["targetResolutionAvailable"]?.ToObject<bool>() ?? false);
        }
    }
}
