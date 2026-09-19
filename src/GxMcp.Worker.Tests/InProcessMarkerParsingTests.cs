using System.Reflection;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // In-process builds forward the GeneXus section-marker protocol
    // (>S/>E0/>E1) rather than MSBuild.exe's "Specifying X..."/"Compiling"
    // text. HandleLine must parse the markers so the in-process path emits
    // phase progress and a named failure. Regression guard for the build-all
    // "no phases / never terminalizes" report against v2.25.1.
    public class InProcessMarkerParsingTests
    {
        private static void InvokeHandleLine(BuildService svc, BuildService.BuildTaskStatus status, string line, bool isError = false)
        {
            var mi = typeof(BuildService).GetMethod("HandleLine", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(mi);
            mi.Invoke(svc, new object[] { status, line, isError });
        }

        [Theory]
        [InlineData("Specify", "Specifying")]
        [InlineData("Generate", "Generating")]
        [InlineData("Compilation", "Compiling")]
        [InlineData("Copying", "Finishing")]
        [InlineData("WebAppConfig", "Finishing")]
        [InlineData("DeveloperMenu", "Finishing")]
        [InlineData("Build", null)]        // outer wrapper — must not churn the phase
        [InlineData("Default", null)]      // unknown section — leave phase alone
        public void MapSectionToPhase_MapsKnownSections(string section, string expected)
        {
            Assert.Equal(expected, BuildService.MapSectionToPhase(section));
        }

        [Fact]
        public void HandleLine_SectionStartMarker_AdvancesPhase()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "sec-start", Phase = "Starting" };

            InvokeHandleLine(svc, status, ">SCompilation:-:Compiling the KB");

            Assert.Equal("Compiling", status.Phase);
        }

        [Fact]
        public void HandleLine_SectionStartMarker_UnknownSection_LeavesPhaseUnchanged()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "sec-unknown", Phase = "Generating" };

            InvokeHandleLine(svc, status, ">SDefault:-:Default model");

            Assert.Equal("Generating", status.Phase);
        }

        [Fact]
        public void HandleLine_SectionFailMarker_RecordsNamedPhaseFailure()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "sec-fail" };

            InvokeHandleLine(svc, status, ">E0Compilation:-:failed");

            Assert.NotNull(status.PhaseFailure);
            Assert.Equal("Compilation", status.PhaseFailure.Name);
        }

        [Fact]
        public void HandleLine_OuterBuildFailMarker_DoesNotSetPhaseFailure()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "outer-fail" };

            InvokeHandleLine(svc, status, ">E0Build");

            Assert.Null(status.PhaseFailure);
        }

        [Fact]
        public void HandleLine_SectionMarkers_AreNotCountedAsErrorsOrWarnings()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "no-miscount" };

            InvokeHandleLine(svc, status, ">SBuild");
            InvokeHandleLine(svc, status, ">SCompilation");
            InvokeHandleLine(svc, status, ">E0Compilation");
            InvokeHandleLine(svc, status, ">E1Copying");

            Assert.Equal(0, status.ErrorCount);
            Assert.Equal(0, status.WarningCount);
        }

        [Fact]
        public void HandleLine_AmbiguousObjectDiagnostic_IsCountedAsErrorEvenWithoutErrorPrefix()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus { TaskId = "ambiguous-object" };

            InvokeHandleLine(svc, status, ">W'SampleEntity' é um nome Objeto ambíguo. Pode se referir a Table ou Transaction.");

            Assert.Equal(1, status.ErrorCount);
            Assert.Contains("ambíguo", status.Errors[0]);
        }
    }
}
