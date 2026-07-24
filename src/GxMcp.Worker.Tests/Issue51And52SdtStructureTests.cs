using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issues #51/#52: the SDK-bound work (collection-preserving clone via ObjectService
    // .CloneSdtStructurePart, and the update_visual SDT authoring path in
    // SDTService.UpdateSDTStructure / SyncSdtJsonNodes) is exercised by the live-KB verification
    // recorded in the PR — the SDT structure root / AddItem / DomainBasedOn / GX_SDT re-bind all
    // require a real KBModel. These cover the KB-independent member-classification logic that
    // decides how a payload member is bound.
    public class Issue51And52SdtStructureTests
    {
        [Theory]
        [InlineData("VarChar")]
        [InlineData("Numeric")]
        [InlineData("Character")]
        [InlineData("Date")]
        [InlineData("DateTime")]
        [InlineData("Boolean")]
        [InlineData("Blob")]
        [InlineData("Image")]
        [InlineData("")]
        [InlineData(null)]
        public void LooksLikePrimitiveType_Primitives_AreFlagged(string typeStr)
        {
            // A primitive (or empty) type token must NOT be treated as an SDT reference, so the
            // writer applies it as a base eDBType rather than trying to resolve an SDT object.
            Assert.True(SDTService.LooksLikePrimitiveType(typeStr));
        }

        [Theory]
        [InlineData("DASDTCursosAluno")]
        [InlineData("SomeOtherSdt")]
        public void LooksLikePrimitiveType_NonPrimitiveNames_AreNot(string typeStr)
        {
            // A non-primitive token names an SDT — the writer resolves it and binds as GX_SDT.
            Assert.False(SDTService.LooksLikePrimitiveType(typeStr));
        }
    }
}
