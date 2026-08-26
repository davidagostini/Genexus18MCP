using System;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class TextPayloadGuardTests
    {
        [Fact]
        public void Analyze_LeadingCommentWithLiteralCrLf_IsRejected()
        {
            var issue = TextPayloadGuard.Analyze("// header\\r\\nDoSomething()\\r\\n");

            Assert.NotNull(issue);
            Assert.False(issue.HasActualLineBreaks);
            Assert.True(issue.StartsWithLineComment);
            Assert.Contains("\\r\\n", issue.LiteralSequences);
        }

        [Fact]
        public void Analyze_ActualLineBreaks_AreAccepted()
        {
            var issue = TextPayloadGuard.Analyze("// header\r\nDoSomething()\r\n");

            Assert.Null(issue);
        }

        [Theory]
        [InlineData("msg('\\r\\n')")]
        [InlineData("msg(\"\\n\")")]
        public void Analyze_LiteralSequencesInsideStrings_AreAccepted(string source)
        {
            var issue = TextPayloadGuard.Analyze(source);

            Assert.Null(issue);
        }

        [Fact]
        public void Analyze_LiteralLineBreakOutsideString_IsRejected()
        {
            var issue = TextPayloadGuard.Analyze("DoA()\\nDoB()");

            Assert.NotNull(issue);
            Assert.Contains("\\n", issue.LiteralSequences);
        }

        [Fact]
        public void Analyze_LiteralSequenceInCommentAfterActualLineBreak_IsAccepted()
        {
            var issue = TextPayloadGuard.Analyze("// header\r\n// documentation mentions \\n");

            Assert.Null(issue);
        }

        [Fact]
        public void BuildWriteError_ReturnsStructuredNonPersistingDiagnostic()
        {
            var response = JObject.Parse(TextPayloadGuard.BuildWriteError(
                "P",
                "Source",
                "content",
                "// header\\r\\nDoSomething()"));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
            Assert.Equal("P", response["target"]?.ToString());
            Assert.Equal("Source", response["part"]?.ToString());
            Assert.Equal("content", response["field"]?.ToString());
            Assert.Equal("\\r\\n", response["literalSequences"]?[0]?.ToString());
        }

        [Fact]
        public void ApplyPatch_InvalidPayload_IsRejectedBeforeObjectLookup()
        {
            var response = JObject.Parse(new PatchService(null, null).ApplyPatch(
                "P",
                "Source",
                "Replace",
                "// header\\r\\nDoSomething()",
                "DoSomething()"));

            Assert.Equal("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
            Assert.Equal("content", response["field"]?.ToString());
        }

        [Fact]
        public void ApplyPatch_LiteralSequenceInContext_DoesNotTriggerWriteGuard()
        {
            var response = JObject.Parse(new PatchService(null, null).ApplyPatch(
                "P",
                "Source",
                "Replace",
                "new text",
                "// documentation mentions \\n"));

            Assert.NotEqual("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void FullWrite_InvalidPayload_IsRejectedBeforeObjectLookup()
        {
            var response = JObject.Parse(new WriteService(null).WriteObject(
                "P",
                "Source",
                "// header\\nDoSomething()"));

            Assert.Equal("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
            Assert.Equal("content", response["field"]?.ToString());
        }

        [Fact]
        public void FullWrite_ExplicitBase64ContainingLiteralLineBreaks_IsRejectedAfterDecode()
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("// header\\r\\nDoSomething()"));
            var response = JObject.Parse(new WriteService(null).WriteObject(
                "P", "Source", encoded, null, true, false, true, false, true));

            Assert.Equal("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
            Assert.Equal("content", response["field"]?.ToString());
        }

        [Fact]
        public void AtomicAuthoring_InvalidPayload_IsRejectedBeforeObjectLookup()
        {
            var response = JObject.Parse(new AtomicAuthoringService(null, null, null, null).Run(new JObject
            {
                ["name"] = "P",
                ["type"] = "Procedure",
                ["source"] = "// header\\r\\nDoSomething()"
            }));

            Assert.Equal("AtomicPreflightFailed", response["error"]?["code"]?.ToString());
            Assert.Contains(response["diagnostics"] as JArray, diagnostic =>
                diagnostic["code"]?.ToString() == "LiteralLineBreaksDetected"
                && diagnostic["member"]?.ToString() == "source");
        }

        [Fact]
        public void BatchEdit_InvalidDirectPayload_IsRejectedBeforeObjectLookup()
        {
            var response = JObject.Parse(new BatchService(null, null, null, null).BatchEdit("P", new JArray
            {
                new JObject { ["part"] = "Source", ["content"] = "// header\\r\\nDoSomething()" },
                new JObject { ["part"] = "Rules", ["content"] = "DoSomething();" }
            }));

            Assert.Equal("LiteralLineBreaksDetected", response["error"]?["code"]?.ToString());
            Assert.Equal("Source", response["part"]?.ToString());
        }

        [Fact]
        public void ParseAndValidate_LiteralSourceLineBreaks_AreFieldValidationErrors()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "P",
                ["source"] = "// header\\r\\nDoSomething()"
            });

            Assert.Contains(spec.Errors, error =>
                error["field"]?.ToString() == "source"
                && error["code"]?.ToString() == "LiteralLineBreaksDetected");
        }

        [Fact]
        public void ParseAndValidate_LiteralRuleLineBreaks_AreFieldValidationErrors()
        {
            var spec = AtomicCreateService.ParseAndValidate(new JObject
            {
                ["type"] = "Procedure",
                ["name"] = "P",
                ["rules"] = new JArray("// header\\r\\nDoSomething()")
            });

            Assert.Contains(spec.Errors, error =>
                error["field"]?.ToString() == "rules"
                && error["code"]?.ToString() == "LiteralLineBreaksDetected");
        }
    }
}
