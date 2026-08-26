using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Item 28 (mcp-improvements-2026-05-22, Tier-S, EXPERIMENTAL) — decision-logic
    // tests for fastIncremental. The actual SDK-deep SpecifyOneOnly skip is
    // exercised behind IFastIncrementalDecision; these tests cover the wiring
    // through BuildService + the default heuristic via a fake implementation
    // injected with SetFastIncrementalDecision.
    public class FastIncrementalDecisionTests : BuildServiceTestBase
    {
        private sealed class FakeFastIncrementalDecision : IFastIncrementalDecision
        {
            public FastIncrementalDecision Result { get; set; } = new FastIncrementalDecision();
            public List<string> SeenTargets { get; private set; }
            public FastIncrementalDecision Decide(string kbPath, IReadOnlyList<string> targets)
            {
                SeenTargets = targets?.ToList();
                return Result;
            }
        }

        [Fact]
        public void EmptyDirtySet_returns_nothingDirty_envelope_without_dispatching_build()
        {
            var fake = new FakeFastIncrementalDecision
            {
                Result = new FastIncrementalDecision { NothingDirty = true }
            };
            var svc = new BuildService();
            svc.SetFastIncrementalDecision(fake);

            string json = svc.Build("Build", "FooProc", "none", 200, false, null, fastIncremental: true);

            Assert.Contains("\"status\":\"NoBuildNeeded\"", json);
            Assert.Contains("\"nothingDirty\":true", json);
            Assert.NotNull(fake.SeenTargets);
            Assert.Equal("FooProc", fake.SeenTargets.Single());
        }

        [Fact]
        public void DirtyTheme_forces_full_build_with_fallback_reason()
        {
            var fake = new FakeFastIncrementalDecision
            {
                Result = new FastIncrementalDecision
                {
                    ForceFullBuild = true,
                    FallbackReason = "non-incremental-target-kind"
                }
            };
            var svc = new BuildService();
            svc.SetFastIncrementalDecision(fake);

            string json = svc.Build("Build", "MyTheme", "none", 200, false, null, fastIncremental: true);

            Assert.Contains("\"status\":\"Accepted\"", json);
            Assert.Contains("\"fastIncrementalFallback\":true", json);
            Assert.Contains("\"fallbackReason\":\"non-incremental-target-kind\"", json);
            // No "fastIncremental":{...} block — we fell back.
            Assert.DoesNotContain("\"canSkipDeploy\":true", json);
        }

        [Fact]
        public void DirtyProcedureOnly_emits_canSkipDeploy_and_canSkipSpecify()
        {
            var fake = new FakeFastIncrementalDecision
            {
                Result = new FastIncrementalDecision
                {
                    CanSkipDeploy = true,
                    CanSkipSpecify = new List<string> { "CleanProc" }
                }
            };
            var svc = new BuildService();
            svc.SetFastIncrementalDecision(fake);

            string json = svc.Build("Build", "DirtyProc,CleanProc", "none", 200, false, null, fastIncremental: true);

            Assert.Contains("\"status\":\"Accepted\"", json);
            Assert.Contains("\"canSkipDeploy\":true", json);
            Assert.Contains("\"canSkipSpecify\":[\"CleanProc\"]", json);
            // No fallback signal when the fast path is chosen.
            Assert.DoesNotContain("\"fastIncrementalFallback\":true", json);
        }

        [Fact]
        public void Default_impl_returns_nothingDirty_when_tracker_is_clean()
        {
            // Use a fresh, never-dirtied target — but ensure Mark+Clean primes the
            // "ever built" map so IsDirty returns false.
            string kbPath = @"C:\fake-kb-for-decision-test";
            string target = "TgtX_" + System.Guid.NewGuid().ToString("N").Substring(0, 6);
            EditDirtyTracker.MarkDirty(kbPath, target);
            EditDirtyTracker.MarkClean(kbPath, target);

            var decision = new DefaultFastIncrementalDecision(null);
            // index=null → kind lookup returns null → "target-kind-unknown" if dirty;
            // here it's clean so NothingDirty wins first.
            var result = decision.Decide(kbPath, new List<string> { target });

            Assert.True(result.NothingDirty);
            Assert.False(result.ForceFullBuild);
        }
    }
}
