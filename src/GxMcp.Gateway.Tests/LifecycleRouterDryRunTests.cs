using System;
using GxMcp.Gateway.Routers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class LifecycleRouterDryRunTests
    {
        [Fact]
        public void ConvertToolCall_LifecycleBuild_PropagatesDryRunAndDeploy()
        {
            var router = new SystemRouter();
            var args = new JObject
            {
                ["action"] = "build",
                ["target"] = "Customer,Invoice",
                ["includeCallees"] = "none",
                ["dryRun"] = true,
                ["deploy"] = true
            };

            var routed = router.ConvertToolCall("genexus_lifecycle", args);
            Assert.NotNull(routed);

            var jobj = JObject.FromObject(routed);
            Assert.Equal("Build", jobj["module"]?.ToString());
            Assert.Equal("Build", jobj["action"]?.ToString());
            Assert.Equal("Customer,Invoice", jobj["target"]?.ToString());
            Assert.Equal("none", jobj["includeCallees"]?.ToString());
            Assert.True(jobj["dryRun"]?.Value<bool>());
            Assert.True(jobj["deploy"]?.Value<bool>());
        }

        [Fact]
        public void ConvertToolCall_LifecycleRebuild_PropagatesDryRunAndDeploy()
        {
            var router = new SystemRouter();
            var args = new JObject
            {
                ["action"] = "rebuild",
                ["target"] = "Customer",
                ["includeCallees"] = "transitive",
                ["dryRun"] = true,
                ["deploy"] = true
            };

            var routed = router.ConvertToolCall("genexus_lifecycle", args);
            Assert.NotNull(routed);

            var jobj = JObject.FromObject(routed);
            Assert.Equal("Build", jobj["module"]?.ToString());
            Assert.Equal("RebuildAll", jobj["action"]?.ToString());
            Assert.True(jobj["dryRun"]?.Value<bool>());
            Assert.True(jobj["deploy"]?.Value<bool>());
        }

        [Fact]
        public void ConvertToolCall_LifecycleSpecify_PropagatesDryRun()
        {
            var router = new SystemRouter();
            var args = new JObject
            {
                ["action"] = "specify",
                ["target"] = "Customer",
                ["dryRun"] = true
            };

            var routed = router.ConvertToolCall("genexus_lifecycle", args);
            Assert.NotNull(routed);

            var jobj = JObject.FromObject(routed);
            Assert.Equal("Build", jobj["module"]?.ToString());
            Assert.Equal("Specify", jobj["action"]?.ToString());
            Assert.True(jobj["dryRun"]?.Value<bool>());
        }

        [Fact]
        public void ConvertToolCall_LifecycleCompileCheck_PropagatesDryRun()
        {
            var router = new SystemRouter();
            var args = new JObject
            {
                ["action"] = "build",
                ["mode"] = "compile_check",
                ["target"] = "Customer",
                ["dryRun"] = true
            };

            var routed = router.ConvertToolCall("genexus_lifecycle", args);
            Assert.NotNull(routed);

            var jobj = JObject.FromObject(routed);
            Assert.Equal("Build", jobj["module"]?.ToString());
            Assert.Equal("CompileCheck", jobj["action"]?.ToString());
            Assert.True(jobj["dryRun"]?.Value<bool>());
        }
    }
}
