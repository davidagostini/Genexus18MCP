using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #33: WebSession variable typing (problem B) and SDT-typed collection members
    // (problem A). The SDK-bound persistence (AttCustomType construction, GX_SDT item binding)
    // is exercised by the live KB verification recorded in the PR; these cover the
    // KB-independent resolution surface that decides whether those paths are reached at all.
    public class Issue33WebSessionAndSdtCollectionTests
    {
        // ── Problem B: WebSession is recognized as a built-in user-defined type ──

        [Theory]
        [InlineData("WebSession")]
        [InlineData("websession")]
        [InlineData("  WebSession  ")]
        public void IsBuiltinUserDefinedType_WebSession_IsRecognized(string typeName)
        {
            Assert.True(VariableInjector.IsBuiltinUserDefinedType(typeName));
        }

        [Theory]
        [InlineData("Character")]
        [InlineData("SdtFoo")]
        [InlineData("")]
        [InlineData(null)]
        public void IsBuiltinUserDefinedType_NonBuiltins_AreNot(string typeName)
        {
            Assert.False(VariableInjector.IsBuiltinUserDefinedType(typeName));
        }

        [Theory]
        [InlineData("websession", "WebSession")]
        [InlineData("WEBSESSION", "WebSession")]
        public void CanonicalUserDefinedTypeName_NormalisesCasing(string input, string expected)
        {
            Assert.Equal(expected, VariableInjector.CanonicalUserDefinedTypeName(input));
        }

        [Fact]
        public void CanonicalUserDefinedTypeName_Unknown_ReturnsNull()
        {
            Assert.Null(VariableInjector.CanonicalUserDefinedTypeName("HttpRequest"));
        }

        // The resolver must accept a bare "WebSession" (Recognized, as a DomainReference) so the
        // add/modify paths reach BuildResolvedVariableInto where the external-type bind happens —
        // rather than rejecting it up front with UnknownType.
        [Fact]
        public void VariableTypeResolver_WebSession_ResolvesAsRecognizedReference()
        {
            var res = VariableTypeResolver.Resolve("WebSession");
            Assert.True(res.Recognized);
            Assert.Equal("DomainReference", res.CanonicalType);
            Assert.Equal("WebSession", res.DomainName);
        }

        // ── issue #45: any built-in GeneXus data type name must pass the resolver gate ──
        // The resolver has no KB access, so it can't confirm HttpClient/HttpRequest/… are real
        // GeneXus types — that resolution happens later via VariableInjector.TryBindGenexusDataType
        // (DataTypeProvider.GetTypeByName), exercised by the live-KB verification in the PR. What
        // MUST hold here is that these names are accepted as a recognized reference so the add /
        // modify / DSL paths REACH the SDK bind instead of being rejected up front with UnknownType
        // (the original blocker) — or silently coerced to NUMERIC(4) / a dangling DomainReference.
        [Theory]
        [InlineData("HttpClient")]
        [InlineData("HttpRequest")]
        [InlineData("HttpResponse")]
        [InlineData("MailMessage")]
        [InlineData("Location")]
        [InlineData("Geolocation")]
        // issue #46: the "Properties" data type is just another built-in — it must survive the
        // resolver gate the same way so add/modify/DSL reach the SDK bind (was persisting NUMERIC(4)).
        [InlineData("Properties")]
        public void VariableTypeResolver_GenexusDataTypes_ResolveAsRecognizedReference(string typeName)
        {
            var res = VariableTypeResolver.Resolve(typeName);
            Assert.True(res.Recognized);
            Assert.Equal("DomainReference", res.CanonicalType);
            Assert.Equal(typeName, res.DomainName);
        }

        // ── issue #45 follow-up: &-tokens inside string literals / comments are DATA, not vars ──
        // The auto-declare scanner must not see an ampersand that lives in a quoted string (URL
        // query, HTML entity) or a comment, or it declares spurious VARCHAR(100) variables.

        [Fact]
        public void StripLiteralsAndComments_BlanksAmpersandsInStringLiterals()
        {
            var code = "&url = \"https://x/y?a=1&status=paid&b=2\"\n&html = \"a&nbsp;b\"\n&real = &other";
            var masked = VariableInjector.StripLiteralsAndComments(code);
            // Real code ampersands survive; literal ones are gone.
            Assert.Contains("&url", masked);
            Assert.Contains("&html", masked);
            Assert.Contains("&real", masked);
            Assert.Contains("&other", masked);
            Assert.DoesNotContain("status", masked);
            Assert.DoesNotContain("nbsp", masked);
        }

        [Fact]
        public void StripLiteralsAndComments_BlanksAmpersandsInComments()
        {
            var code = "&keep = 1 // &dropLine comment\n/* &dropBlock */\n&alsoKeep = 2";
            var masked = VariableInjector.StripLiteralsAndComments(code);
            Assert.Contains("&keep", masked);
            Assert.Contains("&alsoKeep", masked);
            Assert.DoesNotContain("dropLine", masked);
            Assert.DoesNotContain("dropBlock", masked);
        }

        [Fact]
        public void StripLiteralsAndComments_HandlesDoubledQuoteEscaping()
        {
            // GeneXus escapes a quote inside a string by doubling it. The string does not end at the
            // doubled quote, so &inside stays masked and &after (real code) survives.
            var code = "&s = \"he said \"\"&inside\"\" ok\"\n&after = 1";
            var masked = VariableInjector.StripLiteralsAndComments(code);
            Assert.Contains("&s", masked);
            Assert.Contains("&after", masked);
            Assert.DoesNotContain("inside", masked);
        }

        // ── issue #47: modify-rollback's non-primitive-binding capture (pure, KB-independent) ──
        // WriteService.ExtractOriginalTypeNameFromDump parses the "&Name : TypeRepr [Collection]"
        // text VariableInjector.GetVariablesAsText emits, to recover the original SDT/BC/built-in
        // type name a failed retype should restore. The full rollback (Variable/VariablesPart/
        // KBObject binding) needs a live SDK — this covers the parsing surface that decides
        // whether the fix's re-bind is attempted at all.

        [Theory]
        [InlineData("&http : HttpClient", "http", "HttpClient")]
        [InlineData("&sess : WebSession", "sess", "WebSession")]
        [InlineData("&items : SdtCandUNIEDU Collection", "items", "SdtCandUNIEDU")]
        [InlineData("&a : Numeric(4)\n&bc : MyBC\n&b : Character(20)", "bc", "MyBC")]
        public void ExtractOriginalTypeNameFromDump_ResolvedBinding_ReturnsTypeName(string dump, string varName, string expected)
        {
            Assert.Equal(expected, WriteService.ExtractOriginalTypeNameFromDump(dump, varName));
        }

        [Theory]
        [InlineData("&s : GX_SDT(4)", "s")]
        [InlineData("&s : GX_BUSCOMP(4)", "s")]
        [InlineData("&s : GX_USRDEFTYP(4)", "s")]
        public void ExtractOriginalTypeNameFromDump_UnresolvedFallbackFormat_ReturnsNull(string dump, string varName)
        {
            // ResolveTypeRepresentation's own "couldn't resolve a bound name" fallback — the
            // caller must not guess a type name from this, so rollback stays scalar-only.
            Assert.Null(WriteService.ExtractOriginalTypeNameFromDump(dump, varName));
        }

        [Fact]
        public void ExtractOriginalTypeNameFromDump_VariableNotInDump_ReturnsNull()
        {
            Assert.Null(WriteService.ExtractOriginalTypeNameFromDump("&other : HttpClient", "missing"));
        }

        [Theory]
        [InlineData(null, "http")]
        [InlineData("", "http")]
        [InlineData("&http : HttpClient", null)]
        [InlineData("&http : HttpClient", "")]
        public void ExtractOriginalTypeNameFromDump_NullOrEmptyInputs_ReturnsNull(string dump, string varName)
        {
            Assert.Null(WriteService.ExtractOriginalTypeNameFromDump(dump, varName));
        }
    }
}
