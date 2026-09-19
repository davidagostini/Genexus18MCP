using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ModularBusinessComponentReferenceTests
    {
        [Theory]
        [InlineData("OrderRecord", "Operations", "OrderRecord", "Operations")]
        [InlineData("Operations.OrderRecord", null, "OrderRecord", "Operations")]
        [InlineData("Operations.OrderRecord", "operations", "OrderRecord", "Operations")]
        public void Normalize_KeepsObjectAndModuleAsSeparateIdentityFields(string input,
            string module, string expectedName, string expectedModule)
        {
            Assert.True(VariableInjector.TryNormalizeBusinessComponentReference(input, module,
                out string name, out string normalizedModule, out string error), error);
            Assert.Equal(expectedName, name);
            Assert.Equal(expectedModule, normalizedModule);
        }

        [Fact]
        public void Normalize_RejectsConflictingQualifiedAndExplicitModules()
        {
            Assert.False(VariableInjector.TryNormalizeBusinessComponentReference(
                "Operations.OrderRecord", "Sales", out _, out _, out string error));
            Assert.Contains("conflicts", error);
        }
    }
}
