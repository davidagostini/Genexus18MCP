using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class GxTestGeneratorServiceTests
    {
        [Fact]
        public void GenerateProcedureUnitTest_UniversalMode_GeneratesSubroutinesAfterEntrypoint()
        {
            var parms = new List<ApiIntrospectService.Parm>
            {
                new ApiIntrospectService.Parm { Name = "CustomerId", Direction = "in", Type = "Numeric(6.0)" },
                new ApiIntrospectService.Parm { Name = "DiscountRate", Direction = "out", Type = "Numeric(4.2)" },
                new ApiIntrospectService.Parm { Name = "IsEligible", Direction = "out", Type = "Boolean" },
                new ApiIntrospectService.Parm { Name = "Messages", Direction = "out", Type = "SDT:Messages" }
            };

            var testPlan = GxTestGeneratorService.GenerateUnitTestCode("CalculateCustomerDiscount", parms, useGxTestAssertModule: false);

            Assert.NotNull(testPlan);
            Assert.Equal("Test_CalculateCustomerDiscount", testPlan.TestObjectName);
            Assert.NotEmpty(testPlan.TestCases);
            Assert.Contains(testPlan.TestCases, tc => tc.CaseType == "HappyPath");
            Assert.Contains(testPlan.TestCases, tc => tc.CaseType == "Boundary");

            Assert.Contains("CalculateCustomerDiscount.Call(", testPlan.GeneratedSource);
            Assert.Contains("msg('ASSERTION FAILED:", testPlan.GeneratedSource);
            Assert.Contains("&IsEligible", testPlan.GeneratedSource);

            // Assert entrypoint comes before Sub definitions
            int doIdx = testPlan.GeneratedSource.IndexOf("Do 'Test_HappyPath'");
            int subIdx = testPlan.GeneratedSource.IndexOf("Sub 'Test_HappyPath'");
            Assert.True(doIdx < subIdx, "Do entrypoint must precede Sub definitions in GeneXus procedures.");
        }

        [Fact]
        public void GenerateProcedureUnitTest_GxTestModuleMode_UsesAssertExternalObject()
        {
            var parms = new List<ApiIntrospectService.Parm>
            {
                new ApiIntrospectService.Parm { Name = "IsActive", Direction = "out", Type = "Boolean" }
            };

            var testPlan = GxTestGeneratorService.GenerateUnitTestCode("CheckActive", parms, useGxTestAssertModule: true);

            Assert.NotNull(testPlan);
            Assert.Contains("Assert.IsTrue(&IsActive", testPlan.GeneratedSource);
        }

        [Fact]
        public void GenerateProcedureUnitTest_NoParameters_GeneratesSimpleExecutionTest()
        {
            var parms = new List<ApiIntrospectService.Parm>();

            var testPlan = GxTestGeneratorService.GenerateUnitTestCode("PurgeTemporaryLogs", parms);

            Assert.NotNull(testPlan);
            Assert.Equal("Test_PurgeTemporaryLogs", testPlan.TestObjectName);
            Assert.Contains("PurgeTemporaryLogs.Call()", testPlan.GeneratedSource);
        }
    }
}
