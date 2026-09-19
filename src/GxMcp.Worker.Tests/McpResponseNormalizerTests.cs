using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class McpResponseNormalizerTests
    {
        [Fact]
        public void NormalizesLegacyStatusAndPreservesDiagnosticFields()
        {
            var response = JObject.Parse(McpResponseNormalizer.Normalize(
                "{\"status\":\"Error\",\"message\":\"Task ID not found\",\"taskId\":\"abc\"}"));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("LegacyWorkerError", response["error"]?["code"]?.ToString());
            Assert.Equal("Task ID not found", response["error"]?["message"]?.ToString());
            Assert.Equal("Error", response["error"]?["legacyStatus"]?.ToString());
            Assert.Equal("abc", response["taskId"]?.ToString());
        }

        [Fact]
        public void NormalizesTopLevelStringErrorAndUsesItsCodeAndHint()
        {
            var response = JObject.Parse(McpResponseNormalizer.Normalize(
                "{\"error\":\"Unknown build taskId\",\"code\":\"BuildTaskNotFound\",\"hint\":\"Poll status\"}"));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("BuildTaskNotFound", response["error"]?["code"]?.ToString());
            Assert.Equal("Unknown build taskId", response["error"]?["message"]?.ToString());
            Assert.Equal("Poll status", response["error"]?["hint"]?.ToString());
        }

        [Fact]
        public void LeavesCanonicalSuccessAndNestedDomainErrorsUntouched()
        {
            const string success = "{\"status\":\"ok\",\"result\":{\"error\":\"domain detail\"}}";

            Assert.Equal(success, McpResponseNormalizer.Normalize(success));
        }

        [Fact]
        public void InvalidJsonGetsCanonicalErrorWithoutEchoingRawPayload()
        {
            var response = JObject.Parse(McpResponseNormalizer.Normalize("not-json"));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("LegacyWorkerError", response["error"]?["code"]?.ToString());
            Assert.DoesNotContain("not-json", response.ToString());
        }
    }
}
