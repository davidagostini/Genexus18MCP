using Xunit;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class VariableTypeResolverTests
    {
        [Theory]
        [InlineData("Character", "Character", null, null)]
        // issue #32 item 4: VarChar is its own canonical type (round-trips to eDBType.VARCHAR).
        [InlineData("VarChar(120)", "VarChar", 120, null)]
        [InlineData("String(50)", "Character", 50, null)]
        [InlineData("Int", "Numeric", null, null)]
        [InlineData("Numeric(10,2)", "Numeric", 10, 2)]
        // issue #31.1: accept the GeneXus IDE dot form for length.decimals too.
        [InlineData("Numeric(9.0)", "Numeric", 9, 0)]
        [InlineData("Numeric(18.4)", "Numeric", 18, 4)]
        [InlineData("Numeric(9)", "Numeric", 9, null)]
        [InlineData("Bool", "Boolean", null, null)]
        [InlineData("DateTime", "DateTime", null, null)]
        public void Resolve_KnownSynonyms_ReturnsCanonical(string input, string expType, int? expLen, int? expDec)
        {
            var r = VariableTypeResolver.Resolve(input);
            Assert.True(r.Recognized);
            Assert.Equal(expType, r.CanonicalType);
            Assert.Equal(expLen, r.Length);
            Assert.Equal(expDec, r.Decimals);
        }

        [Fact]
        public void Resolve_UnknownWithParens_StillRejected()
        {
            // Bare-word fallback should not catch things with bogus (n) syntax — those are typos.
            var r = VariableTypeResolver.Resolve("Bogus(99)");
            Assert.False(r.Recognized);
            Assert.NotNull(r.Suggestion);
            Assert.NotEmpty(r.AcceptedList);
        }

        [Fact]
        public void Resolve_DomainReference_PassesThrough()
        {
            var r = VariableTypeResolver.Resolve("&PesCod");
            Assert.True(r.Recognized);
            Assert.Equal("DomainReference", r.CanonicalType);
            Assert.Equal("PesCod", r.DomainName);
        }

        [Theory]
        // FR#4 (friction-report 2026-05-19): SDT / BC names resolved as DomainReference
        // (legacy name; covers all SDK ResolveTypeObject paths: SDT, BC, Domain). Resolver
        // is KB-blind — WriteService returns UnknownType if SDK can't find it.
        [InlineData("SdtAluUniGraInfo", "SdtAluUniGraInfo")]
        [InlineData("SdtFoo.Item", "SdtFoo.Item")]
        // SDT item / level type: a single element of a collection SDT (e.g. GeneXusCommon's
        // Messages). The dotted name must survive the resolver intact so the write path reaches
        // the item-aware bind (VariableInjector.TryBindSdtItemType → DataTypeProvider.GetTypeByName),
        // which types the variable as the item — NOT the whole collection SDT. Live-verified in PR:
        // "&Message : Messages.Message" now persists as the item, no longer collapsing to "Messages".
        [InlineData("Messages.Message", "Messages.Message")]
        [InlineData("MyBusinessComponent", "MyBusinessComponent")]
        public void Resolve_BareObjectName_AcceptsAsDomainReference(string input, string expectedName)
        {
            var r = VariableTypeResolver.Resolve(input);
            Assert.True(r.Recognized);
            Assert.Equal("DomainReference", r.CanonicalType);
            Assert.Equal(expectedName, r.DomainName);
        }
    }
}
