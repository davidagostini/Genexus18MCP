using System.Linq;
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
    }
}
