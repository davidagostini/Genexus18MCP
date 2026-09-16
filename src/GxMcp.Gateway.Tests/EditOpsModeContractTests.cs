using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Gateway.Routers;

namespace GxMcp.Gateway.Tests
{
    public class EditOpsModeContractTests
    {
        [Fact]
        public void Edit_RoutesOpsMode()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"mode\":\"ops\",\"ops\":[{\"op\":\"set_attribute\",\"name\":\"X\",\"type\":\"Numeric(8.0)\"}]}");
            var msg = router.ConvertToolCall("genexus_edit", args);
            Assert.NotNull(msg);
            var obj = JObject.FromObject(msg!);
            Assert.Equal("SemanticOps", obj["module"]?.ToString());
            Assert.Equal("Apply", obj["action"]?.ToString());
            Assert.Equal("Customer", obj["target"]?.ToString());
            var opsArr = obj["ops"] as JArray;
            Assert.NotNull(opsArr);
            Assert.Single(opsArr!);
        }

        [Fact]
        public void Edit_OpsMode_PropagatesPartAndDryRun()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"part\":\"Structure\",\"mode\":\"ops\",\"dryRun\":true," +
                "\"ops\":[{\"op\":\"add_attribute\",\"name\":\"Foo\",\"type\":\"Numeric(4.0)\"}]}");
            var msg = router.ConvertToolCall("genexus_edit", args);
            var obj = JObject.FromObject(msg!);
            Assert.Equal("Structure", obj["part"]?.ToString());
            Assert.True(obj["dryRun"]?.ToObject<bool>());
        }

        // ── JSON-Patch (RFC 6902) contract tests ──────────────────────────────

        [Fact]
        public void Edit_PatchMode_WithArrayPatch_RoutesToJsonPatch()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"part\":\"Structure\",\"mode\":\"patch\"," +
                "\"patch\":[{\"op\":\"replace\",\"path\":\"/description\",\"value\":\"new\"}]}");
            var msg = router.ConvertToolCall("genexus_edit", args);
            Assert.NotNull(msg);
            var obj = JObject.FromObject(msg!);
            Assert.Equal("JsonPatch", obj["module"]?.ToString());
            Assert.Equal("Apply", obj["action"]?.ToString());
            Assert.Equal("Customer", obj["target"]?.ToString());
            var patchArr = obj["patch"] as JArray;
            Assert.NotNull(patchArr);
            Assert.Single(patchArr!);
        }

        [Fact]
        public void Edit_PatchMode_WithArrayPatch_PropagatesPartAndDryRun()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"part\":\"Structure\",\"mode\":\"patch\",\"dryRun\":true," +
                "\"patch\":[{\"op\":\"replace\",\"path\":\"/description\",\"value\":\"x\"}]}");
            var msg = router.ConvertToolCall("genexus_edit", args);
            var obj = JObject.FromObject(msg!);
            Assert.Equal("JsonPatch", obj["module"]?.ToString());
            Assert.Equal("Structure", obj["part"]?.ToString());
            Assert.True(obj["dryRun"]?.ToObject<bool>());
        }

        [Fact]
        public void Edit_PatchMode_WithStringPatch_RoutesToLegacyPatch()
        {
            // Regression guard: string patch must still route to legacy Patch module.
            var router = new ObjectRouter();
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"mode\":\"patch\",\"patch\":\"some text patch\"}");
            var msg = router.ConvertToolCall("genexus_edit", args);
            Assert.NotNull(msg);
            var obj = JObject.FromObject(msg!);
            Assert.Equal("Patch", obj["module"]?.ToString());
        }

        [Fact]
        public void Edit_PatchMode_NoPatch_RoutesToLegacyPatch()
        {
            // No patch field at all → falls through to legacy text-patch route.
            var router = new ObjectRouter();
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"mode\":\"patch\",\"content\":\"old\",\"context\":\"old\"}");
            var msg = router.ConvertToolCall("genexus_edit", args);
            Assert.NotNull(msg);
            var obj = JObject.FromObject(msg!);
            Assert.Equal("Patch", obj["module"]?.ToString());
        }

        // ── Issues #205/#206: patch.scope / patch.indentation contract ────────

        [Fact]
        public void Edit_AbbreviatedPatch_ForwardsScopeAndIndentation()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"part\":\"Source\",\"mode\":\"patch\",\"patch\":{\"find\":\"a\",\"replace\":\"b\"," +
                "\"scope\":{\"start\":\"// A\",\"end\":\"// B\"},\"indentation\":{\"mode\":\"validate\"}}}");

            var obj = JObject.FromObject(router.ConvertToolCall("genexus_edit", args)!);

            Assert.Equal("Patch", obj["module"]?.ToString());
            Assert.Equal("a", obj["context"]?.ToString());
            Assert.Equal("b", obj["payload"]?.ToString());
            Assert.Equal("// A", obj["scope"]?["start"]?.ToString());
            Assert.Equal("// B", obj["scope"]?["end"]?.ToString());
            Assert.Equal("validate", obj["indentation"]?["mode"]?.ToString());
            // Records that the caller used the abbreviated form: the only form the worker
            // accepts the protections on.
            Assert.True(obj["patchShorthand"]?.ToObject<bool>());
        }

        [Fact]
        public void Edit_ScopeWithoutStart_IsRejectedWithScopeStartRequired()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"mode\":\"patch\",\"patch\":{\"find\":\"a\",\"replace\":\"b\",\"scope\":{\"end\":\"// B\"}}}");

            var ex = Assert.Throws<UsageException>(() => router.ConvertToolCall("genexus_edit", args));
            Assert.Equal("ScopeStartRequired", ex.Code);
        }

        [Theory]
        [InlineData("{\"name\":\"C\",\"mode\":\"patch\",\"operation\":\"Replace\",\"context\":\"a\",\"content\":\"b\",\"scope\":{\"start\":\"// A\"}}")]
        [InlineData("{\"name\":\"C\",\"mode\":\"ops\",\"ops\":[],\"scope\":{\"start\":\"// A\"}}")]
        [InlineData("{\"name\":\"C\",\"mode\":\"patch\",\"targets\":[{\"name\":\"C\",\"content\":\"x\"}],\"scope\":{\"start\":\"// A\"}}")]
        [InlineData("{\"name\":\"C\",\"mode\":\"patch\",\"parts\":[{\"part\":\"Source\",\"content\":\"x\"}],\"scope\":{\"start\":\"// A\"}}")]
        [InlineData("{\"name\":\"C\",\"mode\":\"patch\",\"patch\":{\"find\":\"a\",\"replace\":\"b\",\"scope\":{\"start\":\"// A\"}},\"operation\":\"Insert_After\"}")]
        [InlineData("{\"name\":\"C\",\"mode\":\"patch\",\"patch\":[{\"op\":\"replace\",\"path\":\"/x\",\"value\":\"y\"}],\"scope\":{\"start\":\"// A\"}}")]
        [InlineData("{\"name\":\"C\",\"mode\":\"patch\",\"changeSet\":{\"action\":\"preview\",\"changes\":[]},\"scope\":{\"start\":\"// A\"}}")]
        [InlineData("{\"name\":\"C\",\"mode\":\"patch\",\"patch\":{\"find\":\"a\",\"replace\":\"b\",\"scope\":\"// A\"}}")]
        // A JSON-string patch is not routed as the abbreviated form, so a scope next to it is
        // rejected rather than silently dropped (the string form is never substituted for it).
        [InlineData("{\"name\":\"C\",\"mode\":\"patch\",\"patch\":\"{\\\"find\\\":\\\"a\\\",\\\"replace\\\":\\\"b\\\"}\",\"scope\":{\"start\":\"// A\"}}")]
        public void Edit_ScopeOnUnsupportedForm_IsRejectedWithScopeUnsupportedPatchForm(string json)
        {
            var router = new ObjectRouter();
            var ex = Assert.Throws<UsageException>(() =>
                router.ConvertToolCall("genexus_edit", JObject.Parse(json)));
            Assert.Equal("ScopeUnsupportedPatchForm", ex.Code);
        }

        [Theory]
        [InlineData("{\"name\":\"C\",\"mode\":\"patch\",\"operation\":\"Append\",\"content\":\"b\",\"indentation\":{\"mode\":\"validate\"}}")]
        [InlineData("{\"name\":\"C\",\"mode\":\"patch\",\"targets\":[{\"name\":\"C\",\"content\":\"x\"}],\"indentation\":{\"mode\":\"validate\"}}")]
        public void Edit_IndentationOnUnsupportedForm_IsRejectedWithIndentationUnsupportedPatchForm(string json)
        {
            var router = new ObjectRouter();
            var ex = Assert.Throws<UsageException>(() =>
                router.ConvertToolCall("genexus_edit", JObject.Parse(json)));
            Assert.Equal("IndentationUnsupportedPatchForm", ex.Code);
        }

        [Fact]
        public void Edit_WithoutScopeOrIndentation_IsUnaffected()
        {
            var router = new ObjectRouter();
            var args = JObject.Parse("{\"name\":\"Customer\",\"mode\":\"patch\",\"patch\":{\"find\":\"a\",\"replace\":\"b\"}}");
            var obj = JObject.FromObject(router.ConvertToolCall("genexus_edit", args)!);
            Assert.Equal("Patch", obj["module"]?.ToString());
            // Absent or JSON null — never a populated protection.
            Assert.True(obj["scope"] == null || obj["scope"]!.Type == JTokenType.Null);
            Assert.True(obj["indentation"] == null || obj["indentation"]!.Type == JTokenType.Null);
            // The abbreviated-form marker is set for every {find,replace} patch; the worker
            // only consults it when scope/indentation are actually present.
            Assert.True(obj["patchShorthand"]?.ToObject<bool>());
        }

        [Fact]
        public void Edit_OpsMode_TakesPrecedenceOverPatchAndFull()
        {
            var router = new ObjectRouter();
            // mode=ops should win even if patch-style fields are present
            var args = JObject.Parse(
                "{\"name\":\"Customer\",\"mode\":\"ops\",\"content\":\"ignored\"," +
                "\"ops\":[{\"op\":\"remove_attribute\",\"name\":\"X\"}]}");
            var msg = router.ConvertToolCall("genexus_edit", args);
            var obj = JObject.FromObject(msg!);
            Assert.Equal("SemanticOps", obj["module"]?.ToString());
        }
    }
}
