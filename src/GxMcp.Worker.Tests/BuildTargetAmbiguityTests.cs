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
        public void BuildDryRunFailsClosedWhenTargetIsNotInLoadedIndex()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "Known", Type = "Procedure", Calls = new List<string>() }
            });
            index.MarkIndexComplete(1);
            var build = new BuildService();
            build.SetIndexCacheService(index);

            var response = Newtonsoft.Json.Linq.JObject.Parse(
                build.BuildDryRun("Build", "Missing", "none", 20));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("BuildTargetUnresolved", response["error"]?["code"]?.ToString());
            Assert.Contains("Missing", response["targets"]?.ToObject<string[]>() ?? new string[0]);
            Assert.True(response["targetResolutionAvailable"]?.ToObject<bool>());
        }

        [Fact]
        public void BuildDryRunReportsResolutionStateWhenTargetIsIndexed()
        {
            var index = new IndexCacheService();
            index.LoadFromEntries(new[]
            {
                new SearchIndex.IndexEntry { Name = "Known", Type = "Procedure", Calls = new List<string>() }
            });
            index.MarkIndexComplete(1);
            var build = new BuildService();
            build.SetIndexCacheService(index);

            var response = Newtonsoft.Json.Linq.JObject.Parse(
                build.BuildDryRun("Build", "Procedure:Known", "none", 20));

            Assert.Equal("ok", response["status"]?.ToString());
            Assert.True(response["result"]?["preview"]?["targetResolutionAvailable"]?.ToObject<bool>());
            Assert.Equal(new[] { "Procedure:Known" },
                response["result"]?["preview"]?["wouldBuild"]?.ToObject<string[]>());
        }
    }
}
