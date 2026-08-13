using System.Collections.Generic;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #86: action=specify on an unreachable or not-found object must return
    // structured generateEvidence (ok=false, unreachable/notFound entries, note, and
    // specify-gap warning), allowing the gateway to surface effective_status=SucceededWithGaps.
    public class SpecifyEvidenceTests
    {
        [Fact]
        public void Specify_UnreachableObject_PopulatesGenerateEvidence_AndWarning()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = "test-unreachable",
                Status = "Succeeded",
                Action = "Build",
                SpecifyOnly = true,
                FullLogPath = Path.GetTempFileName()
            };

            File.WriteAllText(status.FullLogPath,
                "========== Specification iniciado ==========\n" +
                "warning spc0217: Object 'UnreachableProc' is unreachable.\n" +
                "> Specification Sucesso\n");

            try
            {
                var attachMethod = typeof(BuildService).GetMethod(
                    "AttachGenerateEvidence",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(attachMethod);

                attachMethod.Invoke(svc, new object[] { status, "Build", new List<string> { "UnreachableProc" } });

                Assert.NotNull(status.GenerateEvidence);
                Assert.False(status.GenerateEvidence["ok"]?.Value<bool>());
                Assert.Equal(1, status.GenerateEvidence["objectsChecked"]?.Value<int>());
                Assert.Equal(0, status.GenerateEvidence["objectsSpecified"]?.Value<int>());

                var unreachable = status.GenerateEvidence["unreachable"] as JArray;
                Assert.NotNull(unreachable);
                Assert.Single(unreachable);
                Assert.Equal("UnreachableProc", unreachable[0]?["object"]?.ToString());
                Assert.Equal("unreachable", unreachable[0]?["reason"]?.ToString());

                Assert.True(status.WarningCount > 0);
                Assert.Contains(status.Warnings, w => w.Contains("[specify-gap]") && w.Contains("UnreachableProc"));
                Assert.NotNull(status.Hint);
                Assert.Contains("UnreachableProc", status.Hint);
            }
            finally
            {
                if (File.Exists(status.FullLogPath)) File.Delete(status.FullLogPath);
            }
        }

        [Fact]
        public void Specify_NotFoundInKnowledgeBase_PopulatesGenerateEvidence_AndWarning()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = "test-notfound",
                Status = "Succeeded",
                Action = "Build",
                SpecifyOnly = true,
                FullLogPath = Path.GetTempFileName()
            };

            File.WriteAllText(status.FullLogPath,
                "warning : Objeto 'GhostProc' não foi encontrado na Knowledge Base.\n" +
                "========== Specification iniciado ==========\n" +
                "> Specification Sucesso\n");

            try
            {
                var attachMethod = typeof(BuildService).GetMethod(
                    "AttachGenerateEvidence",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(attachMethod);

                attachMethod.Invoke(svc, new object[] { status, "Build", new List<string> { "GhostProc" } });

                Assert.NotNull(status.GenerateEvidence);
                Assert.False(status.GenerateEvidence["ok"]?.Value<bool>());

                var notFound = status.GenerateEvidence["notFound"] as JArray;
                Assert.NotNull(notFound);
                Assert.Single(notFound);
                Assert.Equal("GhostProc", notFound[0]?["object"]?.ToString());
                Assert.Equal("notFoundInKnowledgeBase", notFound[0]?["reason"]?.ToString());

                Assert.True(status.WarningCount > 0);
                Assert.Contains(status.Warnings, w => w.Contains("[specify-gap]") && w.Contains("GhostProc"));
            }
            finally
            {
                if (File.Exists(status.FullLogPath)) File.Delete(status.FullLogPath);
            }
        }

        [Fact]
        public void Specify_ReachableCleanObject_PopulatesOkEvidence_NoWarnings()
        {
            var svc = new BuildService();
            var status = new BuildService.BuildTaskStatus
            {
                TaskId = "test-clean",
                Status = "Succeeded",
                Action = "Build",
                SpecifyOnly = true,
                FullLogPath = Path.GetTempFileName()
            };

            File.WriteAllText(status.FullLogPath,
                "========== Specification iniciado ==========\n" +
                "> L Specifying CleanProc (1 of 1) ...\n" +
                "> Specification Sucesso\n");

            try
            {
                var attachMethod = typeof(BuildService).GetMethod(
                    "AttachGenerateEvidence",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(attachMethod);

                attachMethod.Invoke(svc, new object[] { status, "Build", new List<string> { "CleanProc" } });

                Assert.NotNull(status.GenerateEvidence);
                Assert.True(status.GenerateEvidence["ok"]?.Value<bool>());
                Assert.Equal(1, status.GenerateEvidence["objectsChecked"]?.Value<int>());
                Assert.Equal(1, status.GenerateEvidence["objectsSpecified"]?.Value<int>());
                Assert.Null(status.GenerateEvidence["unreachable"]);
                Assert.Null(status.GenerateEvidence["notFound"]);
                Assert.Equal(0, status.WarningCount);
            }
            finally
            {
                if (File.Exists(status.FullLogPath)) File.Delete(status.FullLogPath);
            }
        }
    }
}
