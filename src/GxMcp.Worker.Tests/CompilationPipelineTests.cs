using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class CompilationPipelineTests
    {
        [Fact]
        public void CompilationPipeline_HarvestDiagnostics_ExtractsStructuredErrors()
        {
            var pipeline = new CompilationPipeline();
            string rawLog = @"
C:\Models\Billing\state.cs(45,12): error CS0103: The name 'customerId' does not exist in the current context
spc0005: Attribute CustomerName is not defined in transaction
C:\Models\Billing\invoice.cs(120,5): warning CS0219: The variable 'total' is assigned but its value is never used
";

            var diagnostics = pipeline.HarvestDiagnostics(rawLog);

            Assert.Equal(3, diagnostics.Count);

            var cs0103 = diagnostics.Find(d => d.Code == "CS0103");
            var spc0005 = diagnostics.Find(d => d.Code == "spc0005");
            var cs0219 = diagnostics.Find(d => d.Code == "CS0219");

            Assert.NotNull(cs0103);
            Assert.Equal("error", cs0103.Severity);
            Assert.Equal(45, cs0103.Line);
            Assert.Equal(12, cs0103.Column);
            Assert.Contains("customerId", cs0103.Message);

            Assert.NotNull(spc0005);
            Assert.Equal("error", spc0005.Severity);
            Assert.Contains("CustomerName", spc0005.Message);

            Assert.NotNull(cs0219);
            Assert.Equal("warning", cs0219.Severity);
            Assert.Equal(120, cs0219.Line);
        }

        [Fact]
        public void CompilationPipeline_EnvironmentScope_RestoresOriginalEnvironment()
        {
            string currentEnv = "Production";
            var mockKb = new MockKbService(
                getActive: () => currentEnv,
                setActive: env => { currentEnv = env; }
            );

            using (var scope = new EnvironmentScope(mockKb, "Staging"))
            {
                Assert.Equal("Staging", currentEnv);
            }

            // After disposing the scope, original environment must be restored
            Assert.Equal("Production", currentEnv);
        }

        [Fact]
        public void CompilationPipeline_EnvironmentScope_RestoresOriginalEnvironmentOnException()
        {
            string currentEnv = "Production";
            var mockKb = new MockKbService(
                getActive: () => currentEnv,
                setActive: env => { currentEnv = env; }
            );

            try
            {
                using (var scope = new EnvironmentScope(mockKb, "Staging"))
                {
                    Assert.Equal("Staging", currentEnv);
                    throw new InvalidOperationException("Simulated compilation crash");
                }
            }
            catch (InvalidOperationException)
            {
                // Ignored for test
            }

            Assert.Equal("Production", currentEnv);
        }

        private class MockKbService : IEnvironmentManager
        {
            private readonly Func<string> _getActive;
            private readonly Action<string> _setActive;

            public MockKbService(Func<string> getActive, Action<string> setActive)
            {
                _getActive = getActive;
                _setActive = setActive;
            }

            public string GetActiveEnvironmentName() => _getActive();
            public void SetActiveEnvironment(string name) => _setActive(name);
        }
    }
}
