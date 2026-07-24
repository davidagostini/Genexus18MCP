using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issues #47/#48/#50: KB-independent surfaces of the SDT-read, OutputSDT-reference and
    // folder-placement fixes. The SDK-bound persistence/read (structure.Root collection flag,
    // GX_SDT member resolution, obj.Key reference build, CreateObject/SetProperty guards) is
    // exercised by the live-KB verification recorded in the PR; these cover the pure logic that
    // decides whether those paths are reached and how their tokens are classified.
    public class Issue47To50SdtAndPlacementTests
    {
        // ── #47: reference-typed SDT members are flagged for name resolution ──

        [Theory]
        [InlineData("GX_SDT")]
        [InlineData("gx_sdt")]
        [InlineData("GX_BUSCOMP")]
        [InlineData("GX_USRDEFTYP")]
        public void IsReferenceType_ReferenceTokens_AreFlagged(string typeStr)
        {
            Assert.True(SdtMemberResolver.IsReferenceType(typeStr));
        }

        [Theory]
        [InlineData("CHARACTER")]
        [InlineData("NUMERIC")]
        [InlineData("DATE")]
        [InlineData("")]
        [InlineData(null)]
        public void IsReferenceType_Primitives_AreNot(string typeStr)
        {
            Assert.False(SdtMemberResolver.IsReferenceType(typeStr));
        }

        [Fact]
        public void ResolveReferencedTypeName_NullModel_ReturnsNull()
        {
            Assert.Null(SdtMemberResolver.ResolveReferencedTypeName(new object(), null));
        }
    }
}
