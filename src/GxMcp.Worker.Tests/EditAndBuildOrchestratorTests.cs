using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class EditAndBuildOrchestratorTests
    {
        [Fact]
        public void Orchestrate_ReturnsCompositeEnvelope_WhenAllPhasesSucceed()
        {
            var fakeWrite = new FakeWriteService(JObject.Parse(@"{
                ""status"": ""Ok"",
                ""diff"": ""@@ -1 +1 @@\n-old\n+new""
            }"));
            var fakeAnalyze = new FakeAnalyzeService(JObject.Parse(@"{
                ""status"": ""Ready"",
                ""target"": ""InvoiceProc"",
                ""callers"": [""WebInvoice"", ""ReportInvoice""],
                ""callersTruncated"": false,
                ""riskLevel"": ""Low""
            }"));
            var fakeBuild = new FakeBuildService(JObject.Parse(@"{
                ""status"": ""Accepted"",
                ""taskId"": ""b1c2d3e4""
            }"));

            var orchestrator = new EditAndBuildOrchestrator(fakeWrite, fakeAnalyze, fakeBuild);

            string raw = orchestrator.Orchestrate(new JObject
            {
                ["name"] = "InvoiceProc",
                ["part"] = "Source",
                ["mode"] = "patch",
                ["content"] = "@@ -1 +1 @@\n-old\n+new",
                ["buildIncludeCallees"] = "direct"
            });

            var env = JObject.Parse(raw);
            // v2.8.0: canonical envelope — status is lowercase "ok"; payload under result
            Assert.Equal("ok", env["status"]?.ToString());
            Assert.Equal("EditAndBuildCompleted", env["code"]?.ToString());
            Assert.NotNull(env["result"]?["edit"]);
            Assert.NotNull(env["result"]?["impact"]);
            Assert.NotNull(env["result"]?["build"]);
            Assert.Equal("b1c2d3e4", env["result"]?["build"]?["taskId"]?.ToString());
            Assert.Equal(2, ((JArray)env["result"]!["impact"]!["callers"]!).Count);
        }

        [Fact]
        public void Orchestrate_ShortCircuits_WhenEditFails()
        {
            var fakeWrite = new FakeWriteService(JObject.Parse(@"{
                ""status"": ""Error"",
                ""error"": ""Ambiguous object name"",
                ""alternatives"": [
                    { ""name"": ""InvoiceProc"", ""type"": ""Procedure"" },
                    { ""name"": ""InvoiceProc"", ""type"": ""WebPanel"" }
                ]
            }"));
            var fakeAnalyze = new FakeAnalyzeService(null);
            var fakeBuild = new FakeBuildService(null);

            var orchestrator = new EditAndBuildOrchestrator(fakeWrite, fakeAnalyze, fakeBuild);

            string raw = orchestrator.Orchestrate(new JObject { ["name"] = "InvoiceProc" });

            var env = JObject.Parse(raw);
            // v2.8.0: error envelope — status is "error", error sub-object present
            Assert.Equal("error", env["status"]?.ToString());
            Assert.Equal("EditPhaseFailed", env["error"]?["code"]?.ToString());
            Assert.NotNull(env["edit"]);   // edit block surfaced as extra on Err
            Assert.Null(env["result"]);
            Assert.False(fakeAnalyze.WasCalled);
            Assert.False(fakeBuild.WasCalled);
        }

        [Fact]
        public void Orchestrate_SkipsBuild_WhenImpactReportsNoCallers()
        {
            var fakeWrite = new FakeWriteService(JObject.Parse(@"{ ""status"": ""Ok"" }"));
            var fakeAnalyze = new FakeAnalyzeService(JObject.Parse(@"{
                ""status"": ""Ready"",
                ""callers"": []
            }"));
            var fakeBuild = new FakeBuildService(null);

            var orchestrator = new EditAndBuildOrchestrator(fakeWrite, fakeAnalyze, fakeBuild);

            string raw = orchestrator.Orchestrate(new JObject { ["name"] = "OrphanProc" });
            var env = JObject.Parse(raw);

            // v2.8.0: canonical envelope — payload under result
            Assert.Equal("ok", env["status"]?.ToString());
            Assert.NotNull(env["result"]?["impact"]);
            Assert.NotNull(env["result"]?["build"]);
            Assert.True(env["result"]?["build"]?["skipped"]?.ToObject<bool>() ?? false);
            Assert.False(fakeBuild.WasCalled);
        }

        [Fact]
        public void Orchestrate_UnwrapsNestedGatewayArgsEnvelope()
        {
            var fakeWrite = new FakeWriteService(JObject.Parse(@"{ ""status"": ""Ok"" }"));
            var fakeAnalyze = new FakeAnalyzeService(JObject.Parse(@"{
                ""status"": ""Ready"",
                ""callers"": [""CallerProc""]
            }"));
            var fakeBuild = new FakeBuildService(JObject.Parse(@"{
                ""status"": ""Accepted"",
                ""taskId"": ""task-123""
            }"));

            var orchestrator = new EditAndBuildOrchestrator(fakeWrite, fakeAnalyze, fakeBuild);

            string raw = orchestrator.Orchestrate(new JObject
            {
                ["target"] = "InvoiceProc",
                ["part"] = "Source",
                ["args"] = new JObject
                {
                    ["name"] = "InvoiceProc",
                    ["part"] = "Source",
                    ["mode"] = "full",
                    ["content"] = "parm(out:&Ok);"
                }
            });

            var env = JObject.Parse(raw);
            // v2.8.0: canonical envelope
            Assert.Equal("ok", env["status"]?.ToString());
            Assert.Equal("InvoiceProc", env["target"]?.ToString());
            Assert.Equal("InvoiceProc", fakeWrite.LastTarget);
            Assert.Equal("Source", fakeWrite.LastArgs?["part"]?.ToString());
            Assert.Equal("parm(out:&Ok);", fakeWrite.LastArgs?["content"]?.ToString());
            Assert.Equal("task-123", env["result"]?["build"]?["taskId"]?.ToString());
        }

        [Fact]
        public void Orchestrate_Continues_WhenFullWriteReturnsSuccess()
        {
            var fakeWrite = new FakeWriteService(JObject.Parse(@"{ ""status"": ""Success"" }"));
            var fakeAnalyze = new FakeAnalyzeService(JObject.Parse(@"{
                ""status"": ""Ready"",
                ""callers"": [""CallerProc""]
            }"));
            var fakeBuild = new FakeBuildService(JObject.Parse(@"{
                ""status"": ""Accepted"",
                ""taskId"": ""task-234""
            }"));

            var orchestrator = new EditAndBuildOrchestrator(fakeWrite, fakeAnalyze, fakeBuild);

            string raw = orchestrator.Orchestrate(new JObject
            {
                ["name"] = "InvoiceProc",
                ["part"] = "Source",
                ["content"] = "parm(out:&Ok);"
            });

            var env = JObject.Parse(raw);
            // v2.8.0: canonical envelope
            Assert.Equal("ok", env["status"]?.ToString());
            Assert.Equal("task-234", env["result"]?["build"]?["taskId"]?.ToString());
            Assert.True(fakeAnalyze.WasCalled);
            Assert.True(fakeBuild.WasCalled);
        }

        [Fact]
        public void Orchestrate_SkipsBuild_WhenWriteReportsNoChange()
        {
            var fakeWrite = new FakeWriteService(JObject.Parse(@"{ ""status"": ""Success"", ""noChange"": true }"));
            var fakeAnalyze = new FakeAnalyzeService(null);
            var fakeBuild = new FakeBuildService(null);

            var orchestrator = new EditAndBuildOrchestrator(fakeWrite, fakeAnalyze, fakeBuild);

            string raw = orchestrator.Orchestrate(new JObject
            {
                ["name"] = "InvoiceProc",
                ["part"] = "Source",
                ["content"] = "parm(out:&Ok);"
            });

            var env = JObject.Parse(raw);
            // v2.8.0: NoChange — ok envelope, build skipped flag under result
            Assert.Equal("ok", env["status"]?.ToString());
            Assert.Equal("NoChange", env["code"]?.ToString());
            Assert.True(env["result"]?["build"]?["skipped"]?.ToObject<bool>() ?? false);
            Assert.Equal("Edit produced no persisted change.", env["result"]?["build"]?["reason"]?.ToString());
            Assert.False(fakeAnalyze.WasCalled);
            Assert.False(fakeBuild.WasCalled);
        }

        [Fact]
        public void Orchestrate_ValidateTrue_SpecifiesBeforeCallerBuild()
        {
            var fakeWrite = new FakeWriteService(JObject.Parse(@"{ ""status"": ""Ok"" }"));
            var fakeAnalyze = new FakeAnalyzeService(JObject.Parse(@"{ ""status"": ""Ready"", ""callers"": [] }"));
            var fakeBuild = new FakeBuildService(JObject.Parse(@"{ ""status"": ""ok"", ""code"": ""Specified"" }"));
            var orchestrator = new EditAndBuildOrchestrator(fakeWrite, fakeAnalyze, fakeBuild);

            var env = JObject.Parse(orchestrator.Orchestrate(new JObject
            {
                ["name"] = "InvoiceProc", ["part"] = "Rules", ["content"] = "parm(out:&Ok);",
                ["validate"] = true, ["validationMode"] = "specify", ["rollbackOnFailure"] = true
            }));

            Assert.Equal("ok", env["status"]?.ToString());
            Assert.True(fakeBuild.WasSpecifyCalled);
            Assert.True(env["result"]?["specified"]?.ToObject<bool>() ?? false);
        }

        [Fact]
        public void Orchestrate_AsyncSpecification_ReturnsPollTarget_WithoutStartingCallerBuild()
        {
            var fakeWrite = new FakeWriteService(JObject.Parse(@"{ ""status"": ""Ok"" }"));
            var fakeAnalyze = new FakeAnalyzeService(null);
            var fakeBuild = new FakeBuildService(JObject.Parse(@"{ ""status"": ""Accepted"", ""taskId"": ""spec-123"" }"));
            var orchestrator = new EditAndBuildOrchestrator(fakeWrite, fakeAnalyze, fakeBuild);

            var env = JObject.Parse(orchestrator.Orchestrate(new JObject
            {
                ["name"] = "InvoiceProc", ["part"] = "Source", ["content"] = "// changed",
                ["validate"] = true
            }));

            Assert.Equal("ok", env["status"]?.ToString());
            Assert.Equal("EditValidationAccepted", env["code"]?.ToString());
            Assert.Equal("op:spec-123", env["result"]?["pollTarget"]?.ToString());
            Assert.False(env["result"]?["specified"]?.ToObject<bool>() ?? true);
            Assert.True(fakeBuild.WasSpecifyCalled);
            Assert.False(fakeAnalyze.WasCalled);
            Assert.False(fakeBuild.WasCalled);
        }
    }

    internal class FakeWriteService : IWriteServiceFacade
    {
        private readonly JObject _result;
        public bool WasCalled { get; private set; }
        public string LastTarget { get; private set; }
        public JObject LastArgs { get; private set; }
        public FakeWriteService(JObject result) { _result = result; }
        public string WriteObject(string target, JObject args)
        {
            WasCalled = true;
            LastTarget = target;
            LastArgs = args;
            return _result.ToString();
        }
    }
    internal class FakeAnalyzeService : IAnalyzeServiceFacade
    {
        private readonly JObject _result;
        public bool WasCalled { get; private set; }
        public FakeAnalyzeService(JObject result) { _result = result; }
        public string ImpactAnalysis(string target, bool waitForIndex, int waitTimeoutMs)
        {
            WasCalled = true;
            return _result == null ? "{}" : _result.ToString();
        }
    }
    internal class FakeBuildService : IBuildServiceFacade
    {
        private readonly JObject _result;
        public bool WasCalled { get; private set; }
        public bool WasSpecifyCalled { get; private set; }
        public FakeBuildService(JObject result) { _result = result; }
        public string Build(string action, string target, string includeCallees, int buildPlanCap)
        {
            WasCalled = true;
            return _result == null ? "{}" : _result.ToString();
        }
        public string Specify(string target)
        {
            WasSpecifyCalled = true;
            return _result == null ? "{}" : _result.ToString();
        }
    }
}
