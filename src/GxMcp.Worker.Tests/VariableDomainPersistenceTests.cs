using Xunit;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    // A Variables text projection is not proof of persistence: these tests exercise the
    // raw DomainKey/custom-type gate used before and after Save.
    public class VariableDomainPersistenceTests
    {
        [Fact]
        public void NativeDomainReference_MatchingEntityKey_IsAccepted()
        {
            var type = System.Guid.NewGuid();
            Assert.True(VariableInjector.IsNativeDomainReferenceParts(
                type, 17, type, 17, "1", out var failure));
            Assert.Null(failure);
        }

        [Fact]
        public void NativeDomainReference_DifferentEntityKey_IsRejected()
        {
            var type = System.Guid.NewGuid();
            Assert.False(VariableInjector.IsNativeDomainReferenceParts(
                type, 17, type, 18, "1", out var failure));
            Assert.Contains("DomainKey", failure);
        }

        [Theory]
        [InlineData("dom:IDAutomatico")]
        [InlineData("domain:RootModule.IDManual")]
        public void NativeDomainReference_DisplayOnlyCustomType_IsRejected(string token)
        {
            var type = System.Guid.NewGuid();
            Assert.False(VariableInjector.IsNativeDomainReferenceParts(
                type, 17, type, 17, token, out var failure));
            Assert.Contains("display-only", failure);
        }
    }
}
