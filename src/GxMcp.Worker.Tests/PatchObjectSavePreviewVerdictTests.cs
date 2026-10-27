using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // status/code of a requireObjectSave preview stay ok/Applied because the textual simulation succeeded.
    // That is not permission to write, so the preview also says, at the top of its result, whether the same
    // request can be applied, why not, what was established, and that nothing was written.
    public sealed class PatchObjectSavePreviewVerdictTests
    {
        private static JObject Eligible() => JObject.Parse("{verified:true,auditedSdkBuild:'18.0.16.189595'}");

        private static JObject Blocked(params EventsIsolationFinding[] findings)
            => EventsSaveIsolation.DescribeRefusal(new EventsIsolationRefusalException(findings));

        private static EventsIsolationFinding PinsMissing()
            => EventsIsolationFindings.EvaluateDestination(null, null, @"C:\KB\Synthetic", "Synthetic", false).Single();

        private static EventsIsolationFinding BundleUnaudited()
            => EventsIsolationFindings.EvaluateBundle(
                new System.Collections.Generic.Dictionary<string, string> { ["Artech.Genexus.Common"] = "b" + new string('0', 63) },
                null, new string[0], new string[0], null, EventsSaveIsolation.AuditedBundleIds);

        [Fact]
        public void BlockedByPins_SaysItCannotApply_WhyAndThatNothingWasWritten()
        {
            var body = new JObject();

            PatchPersistenceReceipt.AttachObjectSavePreviewScope(body, Blocked(PinsMissing()), matchCount: 1, baseVersionProvided: true);

            Assert.False(body["canApply"].Value<bool>());
            Assert.False(body["writeAttempted"].Value<bool>());
            Assert.True(body["patchMatch"].Value<bool>());
            Assert.Equal("ObjectSaveIsolationUnverified", body["writeBlocker"].ToString());
            var blocker = Assert.Single((JArray)body["applyBlockers"]);
            Assert.Equal(WriteDestinationGuard.PinsMissingCode, blocker["code"].ToString());
            Assert.Contains("environment that launches this Gateway", blocker["remediation"].ToString());
            string summary = body["previewSummary"].ToString();
            Assert.Contains("BLOCKED", summary);
            Assert.Contains(WriteDestinationGuard.PinsMissingCode, summary);
            Assert.Contains("Nothing was written", summary);
            Assert.Contains("SDK validated nothing", summary);
        }

        [Fact]
        public void EveryIndependentBlockerIsListed_SoTheNextOneIsNotDiscoveredAfterTheFirstFix()
        {
            var body = new JObject();

            PatchPersistenceReceipt.AttachObjectSavePreviewScope(
                body, Blocked(PinsMissing(), BundleUnaudited()), matchCount: 1, baseVersionProvided: true);

            var codes = ((JArray)body["applyBlockers"]).Select(b => b["code"].ToString()).ToArray();
            Assert.Equal(new[] { WriteDestinationGuard.PinsMissingCode, "SdkBundleNotAudited" }, codes);
            Assert.Contains("+1 more in applyBlockers", body["previewSummary"].ToString());
        }

        [Fact]
        public void EveryKindOfBlockerTogether_ComesInTheOrderTheRequestIsJudged()
        {
            // The patch itself first (what it matched, how it is indented), then concurrency, then the isolation.
            var body = new JObject();

            PatchPersistenceReceipt.AttachObjectSavePreviewScope(
                body, Blocked(PinsMissing()), matchCount: 0, baseVersionProvided: false,
                indentationBlocker: PatchPersistenceReceipt.IndentationBlocker("IndentationMismatch", "The replacement is indented differently."));

            var codes = ((JArray)body["applyBlockers"]).Select(b => b["code"].ToString()).ToArray();
            Assert.Equal(
                new[] { "PatchNoMatch", "IndentationMismatch", "BaseVersionRequired", WriteDestinationGuard.PinsMissingCode }, codes);
            Assert.Contains("BLOCKED by PatchNoMatch", body["previewSummary"].ToString());
            Assert.Contains("+3 more in applyBlockers", body["previewSummary"].ToString());
        }

        [Theory]
        [InlineData("IndentationMismatch")]
        [InlineData("IndentationNotComparable")]
        public void AnIndentationRefusalTheRealWriteWouldRaise_IsAnApplyBlockerOfThePreviewToo(string code)
        {
            // patch.indentation.mode=validate refuses the real write; a dry run reports the evidence and goes on,
            // so without this blocker the preview would say "can be applied" for a request the write stops.
            var body = new JObject();

            PatchPersistenceReceipt.AttachObjectSavePreviewScope(
                body, Eligible(), matchCount: 1, baseVersionProvided: true,
                indentationBlocker: PatchPersistenceReceipt.IndentationBlocker(code, "The replacement is indented differently."));

            Assert.False(body["canApply"].Value<bool>());
            Assert.Null(body["writeBlocker"]);   // the isolation is fine; this is not an isolation refusal
            var blocker = Assert.Single((JArray)body["applyBlockers"]);
            Assert.Equal(code, blocker["code"].ToString());
            Assert.Equal("patch", blocker["layer"].ToString());
            Assert.Contains("omit patch.indentation", blocker["remediation"].ToString());
            Assert.Contains("BLOCKED by " + code, body["previewSummary"].ToString());
        }

        [Fact]
        public void ACallerThatSaysNothingAboutTheBaseVersion_GetsCannotApply_NotAnImplicitYes()
        {
            // The argument defaults to "not provided": forgetting it must never read as permission.
            var body = new JObject();

            PatchPersistenceReceipt.AttachObjectSavePreviewScope(body, Eligible(), matchCount: 1);

            Assert.False(body["canApply"].Value<bool>());
            Assert.Equal("BaseVersionRequired", Assert.Single((JArray)body["applyBlockers"])["code"].ToString());
        }

        [Fact]
        public void Eligible_WithBaseVersion_CanApply_ButStillSaysTheSdkValidatedNothing()
        {
            var body = new JObject();

            PatchPersistenceReceipt.AttachObjectSavePreviewScope(body, Eligible(), matchCount: 1, baseVersionProvided: true);

            Assert.True(body["canApply"].Value<bool>());
            Assert.Null(body["applyBlockers"]);
            Assert.Null(body["writeBlocker"]);
            Assert.False(body["writeAttempted"].Value<bool>());
            Assert.False(body["validationExecuted"].Value<bool>());
            Assert.False(body["persistabilityVerified"].Value<bool>());
            string summary = body["previewSummary"].ToString();
            Assert.Contains("can be applied", summary);
            Assert.Contains("Nothing was written", summary);
            Assert.Contains("has not validated the Events source", summary);
        }

        [Fact]
        public void Eligible_WithoutBaseVersion_CannotApplyAsSent()
        {
            var body = new JObject();

            PatchPersistenceReceipt.AttachObjectSavePreviewScope(body, Eligible(), matchCount: 1, baseVersionProvided: false);

            Assert.False(body["canApply"].Value<bool>());
            Assert.Null(body["writeBlocker"]);
            var blocker = Assert.Single((JArray)body["applyBlockers"]);
            Assert.Equal("BaseVersionRequired", blocker["code"].ToString());
            Assert.Equal("concurrency", blocker["layer"].ToString());
            Assert.Contains("expectedVersion", blocker["remediation"].ToString());
        }

        [Fact]
        public void EligibleButNothingMatched_CannotApply()
        {
            var body = new JObject();

            PatchPersistenceReceipt.AttachObjectSavePreviewScope(body, Eligible(), matchCount: 0, baseVersionProvided: true);

            Assert.False(body["canApply"].Value<bool>());
            Assert.Equal("PatchNoMatch", Assert.Single((JArray)body["applyBlockers"])["code"].ToString());
        }

        [Fact]
        public void IdempotentPatch_NeedsNoMatchToBeApplied()
        {
            // An unchanged Events source still goes through the guarded save, so it is applicable when eligible.
            var body = new JObject { ["noContentChange"] = true };

            PatchPersistenceReceipt.AttachObjectSavePreviewScope(body, Eligible(), matchCount: 0, baseVersionProvided: true);

            Assert.True(body["canApply"].Value<bool>());
            Assert.Contains("leaves the Events source unchanged", body["previewSummary"].ToString());
        }

        [Fact]
        public void ReportWithoutStructuredBlockers_IsStillExplainedFromItsBlockerAndReason()
        {
            var body = new JObject();
            var legacy = JObject.Parse("{verified:false,blocker:'ObjectSaveIsolationUnverified',reason:'pins'}");

            PatchPersistenceReceipt.AttachObjectSavePreviewScope(body, legacy, matchCount: 1, baseVersionProvided: true);

            var blocker = Assert.Single((JArray)body["applyBlockers"]);
            Assert.Equal("ObjectSaveIsolationUnverified", blocker["code"].ToString());
            Assert.Equal("pins", blocker["message"].ToString());
            Assert.False(body["canApply"].Value<bool>());
        }

        [Fact]
        public void MissingIsolationReport_IsABlockerNotAnImplicitYes()
        {
            var body = new JObject();

            PatchPersistenceReceipt.AttachObjectSavePreviewScope(body, null, matchCount: 1, baseVersionProvided: true);

            Assert.False(body["canApply"].Value<bool>());
            Assert.Equal("IsolationReportUnavailable", Assert.Single((JArray)body["applyBlockers"])["code"].ToString());
        }

        [Fact]
        public void TheExistingPreviewFieldsAreUnchanged()
        {
            // Clients that already read these keep working: the verdict only adds fields.
            var body = new JObject();
            var isolation = Blocked(PinsMissing());

            PatchPersistenceReceipt.AttachObjectSavePreviewScope(body, isolation, matchCount: 2, baseVersionProvided: true);

            Assert.True(body["patchMatch"].Value<bool>());
            Assert.False(body["isolationEligible"].Value<bool>());
            Assert.False(body["validationExecuted"].Value<bool>());
            Assert.False(body["saveExecuted"].Value<bool>());
            Assert.False(body["persistabilityVerified"].Value<bool>());
            Assert.Equal("ObjectSaveIsolationUnverified", body["writeBlocker"].ToString());
            Assert.Same(isolation, body["objectSaveIsolation"]);
            Assert.Equal(PatchPersistenceReceipt.ObjectSavePreviewScope, body["previewScope"].ToString());
        }
    }
}
