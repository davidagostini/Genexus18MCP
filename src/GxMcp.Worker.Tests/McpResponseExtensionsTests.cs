using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // v2.8.0 — Accepted auto-derives cancelTool / pollTool. Err carries
    // retryAfterMs for transient codes. Both extensions preserve canonical
    // envelope conformance.
    public class McpResponseExtensionsTests
    {
        [Fact]
        public void Err_CarriesRetryAndReconciliationDecisions()
        {
            string raw = McpResponse.Err(
                code: "UnknownCommitState",
                message: "The commit state is unknown.",
                retryable: false,
                reconciliationRequired: true);

            var obj = JObject.Parse(raw);
            Assert.False((bool)obj["error"]["retryable"]);
            Assert.True((bool)obj["error"]["reconciliationRequired"]);
            Assert.True(EnvelopeConformance.Validate(raw).Ok);
        }

        [Fact]
        public void ErrRejectsMalformedCanonicalOptionalFields()
        {
            string bad = "{\"status\":\"error\",\"error\":{\"code\":\"X\",\"message\":\"boom\",\"retryable\":\"yes\",\"nextSteps\":{}}}";
            var result = EnvelopeConformance.Validate(bad);
            Assert.False(result.Ok);
            Assert.Contains(result.Violations, v => v.Contains("retryable must be boolean"));
            Assert.Contains(result.Violations, v => v.Contains("nextSteps must be an array"));
        }

        [Fact]
        public void Accepted_AutoDerivesCancelAndPollTools()
        {
            string raw = McpResponse.Accepted(target: "X", operationId: "op-1", pollTarget: "op:op-1");
            Assert.True(EnvelopeConformance.Validate(raw).Ok);

            var obj = JObject.Parse(raw);
            Assert.Equal("accepted", (string)obj["status"]);
            Assert.Equal("op-1", (string)obj["operationId"]);

            var cancel = obj["cancelTool"] as JObject;
            Assert.NotNull(cancel);
            Assert.Equal("genexus_lifecycle", (string)cancel["tool"]);
            Assert.Equal("cancel", (string)cancel["args"]["action"]);
            Assert.Equal("op:op-1", (string)cancel["args"]["target"]);

            var poll = obj["pollTool"] as JObject;
            Assert.NotNull(poll);
            Assert.Equal("genexus_lifecycle", (string)poll["tool"]);
            Assert.Equal("status", (string)poll["args"]["action"]);
            Assert.Equal("op:op-1", (string)poll["args"]["target"]);
        }

        [Fact]
        public void Accepted_RespectsExplicitShortcuts()
        {
            // A caller can override the auto-derived shortcuts when the
            // poll/cancel routing isn't the standard lifecycle pair.
            var customPoll = new JObject
            {
                ["tool"] = "genexus_lifecycle",
                ["args"] = new JObject { ["action"] = "result", ["target"] = "custom" }
            };
            string raw = McpResponse.Accepted(
                target: "X",
                operationId: "op-2",
                pollTool: customPoll);

            var obj = JObject.Parse(raw);
            Assert.Equal("custom", (string)obj["pollTool"]["args"]["target"]);
            // cancelTool still auto-derives because the caller didn't override it.
            Assert.NotNull(obj["cancelTool"]);
        }

        [Fact]
        public void Accepted_NoOperationId_OmitsShortcuts()
        {
            // Without an operationId the auto-derivation has no anchor —
            // shortcuts must be absent rather than emitted with null targets.
            string raw = McpResponse.Accepted(target: "X", operationId: null);
            var obj = JObject.Parse(raw);
            Assert.Null(obj["cancelTool"]);
            Assert.Null(obj["pollTool"]);
        }

        [Fact]
        public void Err_RetryAfterMs_OnlyEmittedWhenPositive()
        {
            string transient = McpResponse.Err(
                code: "WorkerBooting",
                message: "Worker is booting.",
                retryAfterMs: 5000);
            var t = JObject.Parse(transient);
            Assert.Equal(5000, (int)t["error"]["retryAfterMs"]);
            Assert.True(EnvelopeConformance.Validate(transient).Ok);

            string normal = McpResponse.Err(code: "Boom", message: "boom.");
            Assert.Null(JObject.Parse(normal)["error"]["retryAfterMs"]);

            string nonPositive = McpResponse.Err(code: "Boom", message: "boom.", retryAfterMs: 0);
            Assert.Null(JObject.Parse(nonPositive)["error"]["retryAfterMs"]);

            string negative = McpResponse.Err(code: "Boom", message: "boom.", retryAfterMs: -1);
            Assert.Null(JObject.Parse(negative)["error"]["retryAfterMs"]);
        }
    }
}
