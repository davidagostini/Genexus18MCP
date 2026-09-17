using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public sealed class WorkerStartupDiagnosticTests
    {
        [Theory]
        [InlineData("GXMCP_SDK_COMPATIBLE version=18.0.16")]
        [InlineData("GXMCP_SDK_COMPATIBLE version=18.0.16\nGXMCP_SDK_FINGERPRINT_DRIFT path=Artech.dll")]
        [InlineData("GXMCP_SDK_LEGACY_COMPATIBLE version=10.3")]
        [InlineData("GXMCP_SDK_FINGERPRINT_DRIFT path=Artech.dll")]
        public void InformationalSdkDiagnosticsAreNotFatal(string diagnostic)
        {
            Assert.False(WorkerStartupFailure.IsFatalSdkDiagnostic(diagnostic));
            Assert.True(WorkerStartupFailure.IsInformationalSdkDiagnostic(diagnostic));
        }

        [Theory]
        [InlineData("GXMCP_SDK_VERSION_MISMATCH expectedMajors=17 actualVersion=19.0")]
        [InlineData("GXMCP_SDK_COMPATIBLE version=18.0.16\nGXMCP_SDK_ASSEMBLY_MISSING path=Artech.dll")]
        public void FatalSdkDiagnosticsRemainFatal(string diagnostic)
        {
            Assert.True(WorkerStartupFailure.IsFatalSdkDiagnostic(diagnostic));
            Assert.False(WorkerStartupFailure.IsInformationalSdkDiagnostic(diagnostic));
        }

        [Fact]
        public void ExtractCodeUsesTheFirstSdkCodeInMultilineDiagnostic()
        {
            Assert.Equal("GXMCP_SDK_COMPATIBLE", WorkerStartupFailure.ExtractCode(
                "GXMCP_SDK_COMPATIBLE version=18.0.16\nGXMCP_SDK_FINGERPRINT_DRIFT path=Artech.dll"));
        }
    }
}
