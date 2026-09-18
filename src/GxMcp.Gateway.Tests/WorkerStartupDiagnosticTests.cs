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
            Assert.False(SdkDiagnosticClassifier.IsFatalDiagnostic(diagnostic));
            Assert.True(SdkDiagnosticClassifier.IsInformationalDiagnostic(diagnostic));
        }

        [Theory]
        [InlineData("GXMCP_SDK_VERSION_MISMATCH expectedMajors=17 actualVersion=19.0")]
        [InlineData("GXMCP_SDK_COMPATIBLE version=18.0.16\nGXMCP_SDK_ASSEMBLY_MISSING path=Artech.dll")]
        public void FatalSdkDiagnosticsRemainFatal(string diagnostic)
        {
            Assert.True(SdkDiagnosticClassifier.IsFatalDiagnostic(diagnostic));
            Assert.False(SdkDiagnosticClassifier.IsInformationalDiagnostic(diagnostic));
        }

        [Theory]
        [InlineData("GXMCP_SDK_COMPATIBLE version=18.0.16\nGXMCP_SDK_FINGERPRINT_DRIFT path=Artech.dll", "GXMCP_SDK_COMPATIBLE")]
        [InlineData("GXMCP_SDK_COMPATIBLE major=18 GXMCP_SDK_VERSION_MISMATCH major=19", "GXMCP_SDK_VERSION_MISMATCH")]
        [InlineData("startup crashed before reporting", "WORKER_STARTUP_FAILED")]
        [InlineData("GXMCP_SDK_DEGRADED feature=mirror", "GXMCP_SDK_DEGRADED")]
        public void ClassifyCodeSelectsTheRepresentativeCode(string diagnostic, string expected)
        {
            Assert.Equal(expected, SdkDiagnosticClassifier.ClassifyCode(diagnostic));
        }

        [Fact]
        public void UnknownSdkCodesFailClosedAsFatal()
        {
            Assert.True(SdkDiagnosticClassifier.IsFatalCode("GXMCP_SDK_DEGRADED"));
            Assert.Equal("GXMCP_SDK_DEGRADED", SdkDiagnosticClassifier.ClassifyCode("GXMCP_SDK_DEGRADED feature=mirror"));
        }
    }
}
