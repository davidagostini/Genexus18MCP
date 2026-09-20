using System.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// issue #28 item 13 — build output splits environment/infra errors from the
    /// authored object's spec/code errors so an agent can tell "my code is wrong"
    /// from "the KB can't compile in this environment".
    /// </summary>
    public class BuildErrorCategoryTests
    {
        [Theory]
        [InlineData("error CS2001: Origem 'GxWebServicesConfig.cs' não pôde ser encontrada", "environment")]
        [InlineData("error MSB3245: Não foi possível resolver a referência \"GeneXus.Security.API.Common.dll\"", "environment")]
        [InlineData("error CS0006: Metadata file 'Foo.dll' could not be found", "environment")]
        [InlineData("error NU1101: Unable to find package", "environment")]
        [InlineData("error spc0031: attribute not found", "spec")]
        [InlineData("error gen0022: generation issue", "spec")]
        [InlineData("error src0294: unknown function", "spec")]
        [InlineData("error qry0001: query generation issue", "spec")]
        [InlineData("error gtm0092: Cannot find module to restore", "environment")]
        [InlineData("error mtd0007: metadata file generation failed", "environment")]
        [InlineData("error pmm0012: package management failed", "environment")]
        [InlineData("error rgz0004: reorganization failed", "environment")]
        [InlineData("error rgo0004: reorganization output failed", "environment")]
        [InlineData("error CS0246: type 'Foo' could not be found", "code")]
        [InlineData("error CS1002: ; expected", "code")]
        public void ClassifyErrorCategory_BucketsCorrectly(string line, string expected)
        {
            Assert.Equal(expected, BuildService.ClassifyErrorCategory(line));
        }

        [Fact]
        public void Status_SplitsEnvVsCode_AndHintsWhenOnlyEnvironmental()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "t1", Status = "Failed" };
            status.ErrorsDetailed.Add(new BuildService.ErrorDetail { raw = "error CS2001: 'GxWebServicesConfig.cs' não pôde ser encontrada", category = "environment" });
            status.ErrorsDetailed.Add(new BuildService.ErrorDetail { raw = "error MSB3245: unresolved GeneXus.Security.API.Common.dll", category = "environment" });

            Assert.Equal(2, status.EnvErrorCount);
            Assert.Equal(0, status.CodeErrorCount);
            Assert.NotNull(status.EnvErrorsHint);
            Assert.Contains("environment", status.EnvErrorsHint);
        }

        [Fact]
        public void GtmInfrastructureErrors_AreEnvironmentErrors()
        {
            const string raw = "error gtm0092: Cannot find module to restore";
            var status = new BuildService.BuildTaskStatus { TaskId = "t1-gtm", Status = "Failed" };
            status.ErrorsDetailed.Add(new BuildService.ErrorDetail
            {
                raw = raw,
                category = BuildService.ClassifyErrorCategory(raw)
            });

            Assert.Equal(1, status.EnvErrorCount);
            Assert.Equal(0, status.CodeErrorCount);
            Assert.NotNull(status.EnvErrorsHint);
        }

        [Fact]
        public void Status_NoHint_WhenAuthoredCodeAlsoFailed()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "t2", Status = "Failed" };
            status.ErrorsDetailed.Add(new BuildService.ErrorDetail { raw = "error MSB3245: unresolved dll", category = "environment" });
            status.ErrorsDetailed.Add(new BuildService.ErrorDetail { raw = "error spc0031: attribute not found", category = "spec" });

            Assert.Equal(1, status.EnvErrorCount);
            Assert.Equal(1, status.CodeErrorCount);
            Assert.Null(status.EnvErrorsHint);
            Assert.Single(status.EnvErrors);
            Assert.Single(status.CodeErrors);
        }

        // issue #30 item 1: spc#### in an ungenerated build environment is invariant to the
        // Source. The spec-errors hint must surface (never suppressing the error) and, when
        // environment errors are also present, flag them as likely env-induced.
        [Fact]
        public void SpecErrorsHint_FiresOnSpecErrors_AndFlagsEnvInduced()
        {
            var mixed = new BuildService.BuildTaskStatus { TaskId = "t3", Status = "Failed" };
            mixed.ErrorsDetailed.Add(new BuildService.ErrorDetail { raw = "error spc0031: No relationship found among attributes in group starting at line 13.", category = "spec" });
            mixed.ErrorsDetailed.Add(new BuildService.ErrorDetail { raw = "error MSB3245: unresolved GeneXus.Security.API.Common.dll", category = "environment" });

            Assert.Equal(1, mixed.SpecErrorCount);
            Assert.NotNull(mixed.SpecErrorsHint);
            Assert.Contains("INDUCED", mixed.SpecErrorsHint);

            var specOnly = new BuildService.BuildTaskStatus { TaskId = "t4", Status = "Failed" };
            specOnly.ErrorsDetailed.Add(new BuildService.ErrorDetail { raw = "error spc0031: No relationship found among attributes in group starting at line 13.", category = "spec" });
            Assert.NotNull(specOnly.SpecErrorsHint);
            Assert.Contains("genexus_lifecycle action=validate", specOnly.SpecErrorsHint);
        }

        [Fact]
        public void SpecErrorsHint_NullWhenNoSpecErrors()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "t5", Status = "Failed" };
            status.ErrorsDetailed.Add(new BuildService.ErrorDetail { raw = "error CS0246: type 'Foo' could not be found", category = "code" });
            Assert.Equal(0, status.SpecErrorCount);
            Assert.Null(status.SpecErrorsHint);
        }

        [Fact]
        public void SourceAndQueryFamilies_CountAsSpecErrors()
        {
            var status = new BuildService.BuildTaskStatus { TaskId = "t6", Status = "Failed" };
            status.ErrorsDetailed.Add(new BuildService.ErrorDetail { raw = "error src0294: unknown function", category = "spec" });
            status.ErrorsDetailed.Add(new BuildService.ErrorDetail { raw = "error qry0001: query generation issue", category = "spec" });

            Assert.Equal(2, status.SpecErrorCount);
            Assert.NotNull(status.SpecErrorsHint);
        }
    }
}
