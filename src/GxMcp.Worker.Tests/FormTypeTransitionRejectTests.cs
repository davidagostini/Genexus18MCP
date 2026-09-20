using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Friction 2026-05-28 — PatchService now early-rejects Form type transitions
    // (html ↔ layout) before they reach the SDK so the caller gets a typed
    // FormTypeTransitionUnsupported envelope instead of a generic "Visual write
    // failed". The live path needs a KB; these are source/string conventions
    // that pin the contract.
    public class FormTypeTransitionRejectTests
    {
        [Fact]
        public void TryExtractFormType_FindsTypeOnRootForm()
        {
            string html = "<Form id=\"1\" type=\"html\"><table/></Form>";
            string layout = "<Form id=\"1\" type=\"layout\"><detail/></Form>";
            Assert.Equal("html", WriteService.TryExtractFormType(html));
            Assert.Equal("layout", WriteService.TryExtractFormType(layout));
        }

        [Fact]
        public void TryExtractFormType_RegexFallback_OnInvalidXml()
        {
            // Real-world WebForm bodies often have unbalanced fragments or
            // namespace soup that XmlDocument refuses. The regex fallback
            // must still pick up the type attribute.
            string broken = "<Form type='layout' id=1 ><not><well><formed></not>";
            Assert.Equal("layout", WriteService.TryExtractFormType(broken));
        }

        [Fact]
        public void TryExtractFormType_NullOrEmpty_ReturnsNull()
        {
            Assert.Null(WriteService.TryExtractFormType(null));
            Assert.Null(WriteService.TryExtractFormType(""));
            Assert.Null(WriteService.TryExtractFormType("   "));
        }

        [Fact]
        public void PatchService_TransitionRejectionCodeIsWired_ViaConvention()
        {
            string patchSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services",
                    "PatchService.cs"));
            Assert.Contains("FormTypeTransitionUnsupported", patchSrc);
            Assert.Contains("WebFormXmlHelper.IsVisualPart(partName)", patchSrc);
            Assert.Contains("WriteService.TryExtractFormType", patchSrc);
        }

        [Fact]
        public void WriteService_VisualMessageSpecializesForTransition_ViaConvention()
        {
            string writeSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services",
                    "WriteService.VisualWrite.cs"));
            // The visualMessage local replaces the bare "Visual write failed"
            // with a specific transition message when code == FormTypeTransitionUnsupported.
            Assert.Contains("string visualMessage = \"Visual write failed\"", writeSrc);
            Assert.Contains("Form type transition not supported via this write path", writeSrc);
        }
    }
}
