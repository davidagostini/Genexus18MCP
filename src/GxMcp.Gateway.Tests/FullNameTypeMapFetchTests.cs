using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Unit tests for the gateway's FULL name→type map fetch (the root-cause fix for
    /// the Table-shadow auto-injection). Two testable surfaces were extracted from
    /// the private MaybeFetchFullNameTypeMap's Task.Run so they don't need a live
    /// worker: <see cref="Program.ApplyNameTypeMapFromWorkerResult"/> (canonical-envelope
    /// descent + rebuild of the AutoTypeInjector map) and the once-per-KB gate
    /// (<see cref="Program.TryArmFullNameTypeMapFetch"/> /
    /// <see cref="Program.ReleaseFullNameTypeMapGate"/>).
    ///
    /// Joins the AutoTypeInjectorState collection because applying a map mutates
    /// AutoTypeInjector's shared static name→type dictionary.
    /// </summary>
    [Collection("AutoTypeInjectorState")]
    public class FullNameTypeMapFetchTests
    {
        private const string Kb = "testkb";

        // Wipe AutoTypeInjector state so these tests are independent.
        public FullNameTypeMapFetchTests()
        {
            AutoTypeInjector.ClearAll();
        }

        // ── 1. Canonical McpResponse.Ok envelope → descend into result ───────

        [Fact]
        public void ApplyNameTypeMap_CanonicalEnvelope_UnwrapsNestedResult()
        {
            // The worker wraps GetNameTypeMap's payload in the v2.8.1 canonical
            // envelope { status:"ok", code:"NameTypeMap", result:{ nameTypeMap, totalNames } }.
            // Without descending we'd read the envelope's status:"ok" and miss the map.
            var workerResult = new JObject
            {
                ["status"] = "ok",
                ["code"] = "NameTypeMap",
                ["result"] = new JObject
                {
                    ["nameTypeMap"] = new JObject
                    {
                        ["CustomerTransaction"] = new JArray("Transaction", "Table"),
                        ["MyProc"] = new JArray("Procedure")
                    },
                    ["totalNames"] = 2
                }
            };

            bool applied = Program.ApplyNameTypeMapFromWorkerResult(Kb, workerResult);
            Assert.True(applied);

            // The map was actually applied: the shadow collapse resolved
            // {Transaction, Table} → Transaction.
            AutoTypeInjector.PrimeToolAcceptsType("genexus_edit", true);
            var args = new JObject { ["name"] = "CustomerTransaction" };
            bool injected = AutoTypeInjector.TryInject(Kb, "genexus_edit", args, out string type);
            Assert.True(injected);
            Assert.Equal("Transaction", type);
        }

        // ── 2. Flat payload (no envelope) → passes through ────────────────────

        [Fact]
        public void ApplyNameTypeMap_FlatPayload_PassesThrough()
        {
            var flat = new JObject
            {
                ["nameTypeMap"] = new JObject
                {
                    ["MyWebPanel"] = new JArray("WebPanel")
                }
            };

            Assert.True(Program.ApplyNameTypeMapFromWorkerResult(Kb, flat));

            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);
            var args = new JObject { ["name"] = "MyWebPanel" };
            Assert.True(AutoTypeInjector.TryInject(Kb, "genexus_read", args, out string type));
            Assert.Equal("WebPanel", type);
        }

        // ── 3. Envelope without a map → false, no state change ────────────────

        [Fact]
        public void ApplyNameTypeMap_MissingMap_ReturnsFalse()
        {
            var envelope = new JObject
            {
                ["status"] = "ok",
                ["code"] = "NameTypeMap",
                ["result"] = new JObject { ["totalNames"] = 0 }
            };

            Assert.False(Program.ApplyNameTypeMapFromWorkerResult(Kb, envelope));

            // Nothing applied: the name is not resolvable.
            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);
            var args = new JObject { ["name"] = "Anything" };
            Assert.False(AutoTypeInjector.TryInject(Kb, "genexus_read", args, out _));
        }

        // ── 4. Null worker result → false ─────────────────────────────────────

        [Fact]
        public void ApplyNameTypeMap_NullResult_ReturnsFalse()
        {
            Assert.False(Program.ApplyNameTypeMapFromWorkerResult(Kb, null));
        }

        // ── 5. Gate: once per KB alias ────────────────────────────────────────

        [Fact]
        public void Gate_ArmsOncePerAlias_SecondCallNoOps()
        {
            try
            {
                Assert.True(Program.TryArmFullNameTypeMapFetch("kb-a"));
                Assert.False(Program.TryArmFullNameTypeMapFetch("kb-a"), "second arm for same alias must no-op");
                // A different alias is independent (multi-KB sessions).
                Assert.True(Program.TryArmFullNameTypeMapFetch("kb-b"));
            }
            finally
            {
                Program.ReleaseFullNameTypeMapGate("kb-a");
                Program.ReleaseFullNameTypeMapGate("kb-b");
            }
        }

        // ── 6. Re-arm on failure: release lets the next push retry ────────────

        [Fact]
        public void Gate_ReleaseOnFailure_ReallowsRetry()
        {
            try
            {
                Assert.True(Program.TryArmFullNameTypeMapFetch("kb-fail"));
                // Simulate the fetch failing: the finally block releases the gate.
                Program.ReleaseFullNameTypeMapGate("kb-fail");
                Assert.True(Program.TryArmFullNameTypeMapFetch("kb-fail"),
                    "after a failed fetch the gate must re-arm so the next whoami push retries");
            }
            finally
            {
                Program.ReleaseFullNameTypeMapGate("kb-fail");
            }
        }

        // ── 7. Gate stays armed on success (no retry storm) ───────────────────

        [Fact]
        public void Gate_StaysArmedAfterSuccess()
        {
            try
            {
                Assert.True(Program.TryArmFullNameTypeMapFetch("kb-ok"));
                // Success path never releases — assert the gate is still held.
                Assert.False(Program.TryArmFullNameTypeMapFetch("kb-ok"));
            }
            finally
            {
                Program.ReleaseFullNameTypeMapGate("kb-ok");
            }
        }

        [Fact]
        public void InvalidatedFetch_CannotApplyAnOlderGeneration()
        {
            var oldMap = new JObject
            {
                ["nameTypeMap"] = new JObject
                {
                    ["OldObject"] = new JArray("Procedure")
                }
            };

            Assert.True(Program.TryArmFullNameTypeMapFetch(Kb));
            int oldGeneration = Program.GetFullNameTypeMapGeneration(Kb);
            Program.InvalidateFullNameTypeMap(Kb);

            Assert.False(Program.ApplyNameTypeMapFromWorkerResultIfCurrent(Kb, oldMap, oldGeneration));

            AutoTypeInjector.PrimeToolAcceptsType("genexus_read", true);
            Assert.False(AutoTypeInjector.TryInject(Kb, "genexus_read", new JObject { ["name"] = "OldObject" }, out _));
        }
    }
}
