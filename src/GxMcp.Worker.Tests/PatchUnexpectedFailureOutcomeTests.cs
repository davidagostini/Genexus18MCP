using System;
using System.Threading;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// mode=patch used to report every unexpected exception as a conclusive
    /// failure, even when the SDK write call had already run and the content was
    /// in fact persisted (observed with an OutOfMemoryException on a large part).
    /// These tests pin the three write stages the failure envelope must keep apart.
    /// </summary>
    public class PatchUnexpectedFailureOutcomeTests
    {
        private static string Failure(string details = "Exception of type 'System.OutOfMemoryException' was thrown.")
            => McpResponse.Err(
                code: "Error",
                message: details,
                hint: "Check the operation, context, and part name; then retry.",
                extra: new JObject
                {
                    ["part"] = "Styles",
                    ["operation"] = "replace",
                    ["expectedCount"] = 1,
                    ["matchCount"] = 0
                });

        private static JObject Outcome(PatchWriteStage stage, bool? sdkSaveCompleted)
            => JObject.Parse(PatchPersistenceReceipt.AttachUnexpectedFailureOutcome(
                Failure(), stage, sdkSaveCompleted, "SampleDesignSystem", "Styles", "OutOfMemoryException"));

        [Fact]
        public void BeforeWrite_ReportsAProvableNoWrite()
        {
            var payload = Outcome(PatchWriteStage.BeforeWrite, null);

            Assert.Equal("Error", payload["error"]?["code"]?.ToString());
            Assert.Equal("pre-write", payload["writeStage"]?.ToString());
            Assert.False(payload["writeAttempted"]!.Value<bool>());
            Assert.False(payload["saveAttempted"]!.Value<bool>());
            Assert.False(payload["sdkSaveCompleted"]!.Value<bool>());
            Assert.False(payload["saved"]!.Value<bool>());
            Assert.False(payload["persisted"]!.Value<bool>());
            Assert.True(payload["persistedStateKnown"]!.Value<bool>());
            Assert.Equal("OutOfMemoryException", payload["failureType"]?.ToString());
        }

        [Fact]
        public void DuringWrite_ReportsTheOutcomeAsUnknown()
        {
            var payload = Outcome(PatchWriteStage.DuringWrite, null);

            Assert.Equal("PatchWriteOutcomeUnknown", payload["error"]?["code"]?.ToString());
            Assert.Equal("sdk-write", payload["writeStage"]?.ToString());
            Assert.True(payload["writeAttempted"]!.Value<bool>());
            Assert.True(payload["saveAttempted"]!.Value<bool>());
            // The save result is unknown, so it must not be invented in either direction.
            Assert.Equal(JTokenType.Null, payload["sdkSaveCompleted"]!.Type);
            Assert.Equal(JTokenType.Null, payload["saved"]!.Type);
            Assert.Equal(JTokenType.Null, payload["persisted"]!.Type);
            Assert.False(payload["persistedStateKnown"]!.Value<bool>());
            Assert.True(payload["verificationUnavailable"]!.Value<bool>());
            Assert.False(payload["retrySafe"]!.Value<bool>());
            Assert.False(payload["postSaveVerification"]!["reReadConfirmed"]!.Value<bool>());
            Assert.Equal(JTokenType.Null, payload["postSaveVerification"]!["matches"]!.Type);
        }

        [Fact]
        public void DuringWrite_DoesNotAttemptRollbackAndPointsAtAManualReRead()
        {
            var payload = Outcome(PatchWriteStage.DuringWrite, null);

            Assert.False(payload["rollback"]!["attempted"]!.Value<bool>());
            Assert.False(payload["rollback"]!["saveAttempted"]!.Value<bool>());
            Assert.False(payload["rolledBack"]!.Value<bool>());
            Assert.Contains("Do not retry", payload["manualRecovery"]!.ToString());
            var step = payload["error"]?["nextSteps"]?[0];
            Assert.Equal("genexus_read", step?["tool"]?.ToString());
            Assert.Equal("SampleDesignSystem", step?["args"]?["name"]?.ToString());
            Assert.Equal("Styles", step?["args"]?["part"]?.ToString());
        }

        [Fact]
        public void AfterWrite_KeepsTheReportedSdkSaveAndMarksVerificationUnknown()
        {
            var payload = Outcome(PatchWriteStage.AfterWrite, true);

            Assert.Equal("PatchWriteOutcomeUnknown", payload["error"]?["code"]?.ToString());
            Assert.Equal("post-write", payload["writeStage"]?.ToString());
            Assert.True(payload["sdkSaveCompleted"]!.Value<bool>());
            Assert.True(payload["saved"]!.Value<bool>());
            Assert.Equal(JTokenType.Null, payload["persisted"]!.Type);
            Assert.False(payload["persistedStateKnown"]!.Value<bool>());
            Assert.True(payload["verificationUnavailable"]!.Value<bool>());
        }

        [Fact]
        public void AfterWrite_WithoutAReportedSaveDoesNotClaimPersistenceEither()
        {
            var payload = Outcome(PatchWriteStage.AfterWrite, false);

            Assert.False(payload["sdkSaveCompleted"]!.Value<bool>());
            Assert.False(payload["saved"]!.Value<bool>());
            Assert.Equal(JTokenType.Null, payload["persisted"]!.Type);
            Assert.False(payload["persistedStateKnown"]!.Value<bool>());
        }

        [Fact]
        public void UnexpectedFailureKeepsTheOriginalExceptionDetail()
        {
            var payload = Outcome(PatchWriteStage.AfterWrite, true);

            Assert.Contains("OutOfMemoryException", payload["error"]?["failureDetail"]?.ToString());
            Assert.Contains("OutOfMemoryException", payload["details"]?.ToString());
            Assert.DoesNotContain("then retry", payload["error"]?["hint"]?.ToString());
        }

        [Fact]
        public void ApplyPatch_FailingBeforeTheWriteStatesThatNoSaveWasAttempted()
        {
            // No SDK services: the failure happens in the pre-write read, which is
            // exactly the stage that may still report a provable "not persisted".
            var payload = JObject.Parse(new PatchService(null, null).ApplyPatch(
                target: "SamplePanel",
                partName: "Source",
                operation: "Replace",
                content: "// updated",
                context: "// original"));

            Assert.Equal("error", payload["status"]?.ToString());
            Assert.Equal("pre-write", payload["writeStage"]?.ToString());
            Assert.False(payload["writeAttempted"]!.Value<bool>());
            Assert.False(payload["saveAttempted"]!.Value<bool>());
            Assert.False(payload["persisted"]!.Value<bool>());
            Assert.True(payload["persistedStateKnown"]!.Value<bool>());
        }

        [Fact]
        public void AWriteThatMayHaveRunKeepsTheTargetDirty()
        {
            // The persisted state is unknown, so the target must be treated as
            // written: a later build must not take the compile-only fast path and a
            // sibling patch must still be able to classify its NoMatch as Stale.
            Assert.True(PatchPersistenceReceipt.RequiresDirtyTargetMark(PatchWriteStage.DuringWrite));
            Assert.True(PatchPersistenceReceipt.RequiresDirtyTargetMark(PatchWriteStage.AfterWrite));
        }

        [Fact]
        public void AFailureBeforeTheWriteLeavesTheTargetClean()
        {
            Assert.False(PatchPersistenceReceipt.RequiresDirtyTargetMark(PatchWriteStage.BeforeWrite));
        }

        [Fact]
        public void ApplyPatch_FailingBeforeTheWriteDoesNotMarkTheTargetAsWritten()
        {
            // Guards the other direction of the fix: a pre-write failure proves
            // nothing was written, so it must not dirty the target or stamp a write.
            string target = "PatchClean_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var enteredUtc = DateTime.UtcNow;
            Thread.Sleep(5);

            new PatchService(null, null).ApplyPatch(
                target: target,
                partName: "Source",
                operation: "Replace",
                content: "// updated",
                context: "// original");

            Assert.False(WriteService.WasTargetWrittenSince(target, enteredUtc));
            Assert.DoesNotContain(
                EditDirtyTracker.GetDirty(null),
                name => string.Equals(name, target, StringComparison.OrdinalIgnoreCase));
        }
    }
}
