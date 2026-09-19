using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issues #205/#206: the worker-side half of the fail-closed gate for the opt-in patch
    // protections. The gateway rejects an unsupported form before normalizing it to
    // context/content; this covers the second gate, which must reject the same call even when
    // it reaches the worker by another route (e.g. a direct Patch command) instead of
    // silently dropping the protection — and must do it before any SDK access.
    public class PatchProtectionFormTests
    {
        // No KB is open here, so the call can only get past the gate by failing at the read.
        private const string ReadFailureCode = "PatchReadFailed";

        private static PatchService BuildIsolatedPatchService()
        {
            var indexCache = new IndexCacheService();
            var build = new BuildService();
            var kb = new KbService(indexCache);
            kb.SetBuildService(build);
            build.SetKbService(kb);
            indexCache.SetBuildService(build);
            var obj = new ObjectService(kb, build);
            return new PatchService(obj, new WriteService(obj));
        }

        private static JObject Apply(bool patchShorthand, string operation, JObject scope = null, JObject indentation = null)
        {
            return JObject.Parse(BuildIsolatedPatchService().ApplyPatch(
                "NoSuchObject",
                "Source",
                operation,
                "new line",
                "old line",
                scope: scope,
                indentation: indentation,
                patchShorthand: patchShorthand));
        }

        [Fact]
        public void ScopeWithoutTheAbbreviatedForm_IsRejected()
        {
            var response = Apply(patchShorthand: false, operation: "Replace", scope: JObject.Parse("{\"start\":\"// A\"}"));

            Assert.Equal("ScopeUnsupportedPatchForm", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void IndentationWithoutTheAbbreviatedForm_IsRejected()
        {
            var response = Apply(patchShorthand: false, operation: "Replace", indentation: JObject.Parse("{\"mode\":\"validate\"}"));

            Assert.Equal("IndentationUnsupportedPatchForm", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ScopeAndIndentationTogether_ReportScopeFirst()
        {
            var response = Apply(
                patchShorthand: false,
                operation: "Insert_After",
                scope: JObject.Parse("{\"start\":\"// A\"}"),
                indentation: JObject.Parse("{\"mode\":\"validate\"}"));

            Assert.Equal("ScopeUnsupportedPatchForm", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void AbbreviatedFormWithOmittedOperation_PassesTheGate()
        {
            // An omitted operation normalizes to "replace", so the supported form must reach the
            // read instead of being rejected by the gate.
            var response = Apply(patchShorthand: true, operation: null, scope: JObject.Parse("{\"start\":\"// A\"}"));

            Assert.Equal(ReadFailureCode, response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void AbbreviatedFormWithInsertAfter_IsRejected()
        {
            var response = Apply(patchShorthand: true, operation: "Insert_After", scope: JObject.Parse("{\"start\":\"// A\"}"));

            Assert.Equal("ScopeUnsupportedPatchForm", response["error"]?["code"]?.ToString());
        }

        // The `as JObject` casts in the patch handler would silently drop a non-object
        // protection and apply the patch unbounded; the token-type guard keeps the gateway's
        // "never ignored" guarantee for a call that reaches the worker by another route.
        [Theory]
        [InlineData("{\"scope\":\"// A\"}", "ScopeUnsupportedPatchForm")]
        [InlineData("{\"scope\":[\"// A\"]}", "ScopeUnsupportedPatchForm")]
        [InlineData("{\"indentation\":\"validate\"}", "IndentationUnsupportedPatchForm")]
        [InlineData("{\"indentation\":42}", "IndentationUnsupportedPatchForm")]
        public void NonObjectProtection_IsRejectedInsteadOfDropped(string json, string expectedCode)
        {
            string error = CommandDispatcher.CheckPatchProtectionTokenTypes(JObject.Parse(json), "P");

            Assert.NotNull(error);
            Assert.Equal(expectedCode, JObject.Parse(error)["error"]?["code"]?.ToString());
        }

        [Fact]
        public void AbsentNullOrObjectProtections_PassTheTypeGuard()
        {
            Assert.Null(CommandDispatcher.CheckPatchProtectionTokenTypes(JObject.Parse("{}"), "P"));
            Assert.Null(CommandDispatcher.CheckPatchProtectionTokenTypes(JObject.Parse("{\"scope\":null}"), "P"));
            Assert.Null(CommandDispatcher.CheckPatchProtectionTokenTypes(
                JObject.Parse("{\"scope\":{\"start\":\"// A\"},\"indentation\":{\"mode\":\"validate\"}}"), "P"));
        }

        // ── Issue #206: CheckIndentation unit tests ────────────────────────

        [Fact]
        public void CheckIndentation_MatchingBaseIndent_ValidatesSuccessfully()
        {
            var source = new[] { "before", "\t\tif (condition)", "\t\tend" };
            var outcome = PatchTextEditor.ReplaceWithinScope(
                source, 0, source.Length, new[] { "\t\tif (condition)" }, "\t\tif (otherCondition)", 1, false);

            string code = PatchService.CheckIndentation(source, outcome, "\t\tif (otherCondition)", out string msg, out JObject evidence);

            Assert.Null(code);
            Assert.Null(msg);
            Assert.True(evidence["comparable"]?.Value<bool>());
            var sites = evidence["sites"] as JArray;
            Assert.NotNull(sites);
            Assert.Single(sites);
            Assert.True(sites[0]["matches"]?.Value<bool>());
        }

        [Fact]
        public void CheckIndentation_FindAfterBaseIndent_MatchingReplacement_ValidatesSuccessfully()
        {
            var source = new[] { "before", "\t\tif (condition)", "\t\tend" };
            // Find starts after base indent (\t\t), replace has no indent -> resulting prefix is \t\t
            var outcome = PatchTextEditor.ReplaceWithinScope(
                source, 0, source.Length, new[] { "if (condition)" }, "if (otherCondition)", 1, false);

            string code = PatchService.CheckIndentation(source, outcome, "if (otherCondition)", out string msg, out JObject evidence);

            Assert.Null(code);
            Assert.Null(msg);
            Assert.True(evidence["comparable"]?.Value<bool>());
            var sites = evidence["sites"] as JArray;
            Assert.NotNull(sites);
            Assert.Single(sites);
            Assert.True(sites[0]["matches"]?.Value<bool>());
        }

        [Fact]
        public void CheckIndentation_MismatchIndent_ReturnsIndentationMismatch()
        {
            var source = new[] { "before", "\t\tif (condition)", "\t\tend" };
            var outcome = PatchTextEditor.ReplaceWithinScope(
                source, 0, source.Length, new[] { "\t\tif (condition)" }, "    if (otherCondition)", 1, false);

            string code = PatchService.CheckIndentation(source, outcome, "    if (otherCondition)", out string msg, out JObject evidence);

            Assert.Equal("IndentationMismatch", code);
            Assert.Contains("differs from the matched line", msg);
            Assert.True(evidence["comparable"]?.Value<bool>());
        }

        [Fact]
        public void CheckIndentation_MidContentMatch_ReturnsIndentationNotComparable()
        {
            var source = new[] { "before", "\t\tmsg(\"old\");", "\t\tend" };
            var outcome = PatchTextEditor.ReplaceWithinScope(
                source, 0, source.Length, new[] { "\"old\");" }, "\"new\");", 1, false);

            string code = PatchService.CheckIndentation(source, outcome, "\"new\");", out string msg, out JObject evidence);

            Assert.Equal("IndentationNotComparable", code);
            Assert.False(evidence["comparable"]?.Value<bool>());
        }

        [Fact]
        public void CheckIndentation_BlankFirstLineInReplace_ReturnsIndentationNotComparable()
        {
            var source = new[] { "before", "\t\tif (x)", "\t\tend" };
            var outcome = PatchTextEditor.ReplaceWithinScope(
                source, 0, source.Length, new[] { "\t\tif (x)" }, "   \n\t\tif (y)", 1, false);

            string code = PatchService.CheckIndentation(source, outcome, "   \n\t\tif (y)", out string msg, out JObject evidence);

            Assert.Equal("IndentationNotComparable", code);
            Assert.False(evidence["comparable"]?.Value<bool>());
        }

        [Fact]
        public void CheckIndentation_ReplaceAll_FailsIfAnySiteDiffers()
        {
            var source = new[] { "\tif (x)", "\t\tif (x)" };
            var outcome = PatchTextEditor.ReplaceWithinScope(
                source, 0, source.Length, new[] { "if (x)" }, "\tif (y)", 2, true);

            string code = PatchService.CheckIndentation(source, outcome, "\tif (y)", out string msg, out JObject evidence);

            Assert.Equal("IndentationMismatch", code);
            Assert.True(evidence["comparable"]?.Value<bool>());
        }
    }
}
