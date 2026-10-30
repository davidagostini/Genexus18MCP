using System;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class AtomicVariablePhaseTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void FailedBatchStopsAuthoringEvenWhenVariablesAlreadyExist(int existed)
        {
            var outcomes = new JArray(
                new JObject { ["name"] = "NewRecord", ["status"] = "NotAdded" },
                new JObject { ["name"] = "Broken", ["status"] = "Failed", ["reason"] = "UnknownType" });
            var phases = new JArray();

            var exception = Assert.ThrowsAny<Exception>(() =>
                AtomicAuthoringService.RecordVariablePhase(phases, 0, existed, 1, outcomes));

            Assert.Contains("variables", exception.Message);
            var phase = Assert.IsType<JObject>(Assert.Single(phases));
            Assert.Equal("error", (string)phase["status"]);
            Assert.Equal(0, (int)phase["counts"]["added"]);
            Assert.Equal(existed, (int)phase["counts"]["existed"]);
            Assert.Equal("UnknownType", (string)phase["outcomes"][1]["reason"]);
            // AtomicCreate uses this same result to enter compensation before later phases.
            Assert.Equal("error", (string)AtomicCreateService.DescribeVariablePhase(0, existed, 1, outcomes)["status"]);
        }

        [Theory]
        [InlineData(3, 0)]
        [InlineData(0, 3)]
        public void SuccessfulAndExistingOnlyBatchesContinue(int added, int existed)
        {
            var phases = new JArray();
            AtomicAuthoringService.RecordVariablePhase(phases, added, existed, 0, new JArray());
            Assert.Equal("ok", (string)phases[0]["status"]);
            Assert.Equal(added, (int)phases[0]["counts"]["added"]);
            Assert.Equal(existed, (int)phases[0]["counts"]["existed"]);
        }
    }
}
