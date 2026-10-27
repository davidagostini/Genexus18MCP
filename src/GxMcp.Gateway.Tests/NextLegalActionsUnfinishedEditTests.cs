using System.Linq;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // A follow-up such as Build, preview or undo only makes sense after a patch
    // was persisted. A preview, a preview blocked by the isolation preflight and
    // an asynchronous edit that has not finished persisted nothing yet, so the
    // builder must not point at them. The suggestions are what an agent copies
    // next; a wrong one sends it to compile a KB to "fix" a missing pin.
    public class NextLegalActionsUnfinishedEditTests
    {
        private static JObject EditArgs(bool? dryRun = null)
        {
            var args = new JObject { ["name"] = "SyntheticPanel", ["part"] = "Events", ["mode"] = "patch", ["requireObjectSave"] = true };
            if (dryRun.HasValue) args["dryRun"] = dryRun.Value;
            return args;
        }

        private static JObject Envelope(JObject result) => new JObject { ["status"] = "ok", ["code"] = "Applied", ["result"] = result };

        private static string[] Tools(JArray? suggestions) => suggestions == null
            ? new string[0]
            : suggestions.Select(s => s["tool"]?.ToString() ?? string.Empty).ToArray();

        private static JArray? Follow(JObject args, JObject payload)
            => NextLegalActionsBuilder.BuildFor("genexus_edit", args, payload, isError: false);

        [Fact]
        public void Preview_OfAnEdit_SuggestsNothingBecauseNothingWasApplied()
        {
            var payload = Envelope(new JObject { ["persisted"] = false, ["saved"] = false, ["matchCount"] = 1 });

            Assert.Null(Follow(EditArgs(true), payload));
        }

        // The spellings the write gate accepts as a preview (Program.IsMutationPreview), not an alias of our own.
        [Theory]
        [InlineData("only")]
        [InlineData("validate-only")]
        [InlineData("VALIDATE-ONLY")]
        public void ValidateOnlyRequest_IsAPreview(string validate)
        {
            var args = EditArgs();
            args["validate"] = validate;

            Assert.Null(Follow(args, Envelope(new JObject { ["persisted"] = false })));
        }

        [Theory]
        [InlineData("preview")]
        [InlineData("validate")]
        [InlineData("Preview")]
        public void ChangeSetPreviewOrValidate_IsAPreview(string action)
        {
            var args = EditArgs();
            args["changeSet"] = new JObject { ["action"] = action };

            Assert.Null(Follow(args, Envelope(new JObject { ["persisted"] = false })));
        }

        [Fact]
        public void ChangeSetApply_NextToATopLevelDryRun_IsNotAPreview_SoItsFollowUpsStay()
        {
            // changeSet.action=apply persists whatever a top-level dryRun says (the recovery fence makes the same
            // call), so the Build / preview / undo follow-ups of a real write must not be dropped.
            var args = EditArgs(true);
            args["changeSet"] = new JObject { ["action"] = "apply" };
            var payload = Envelope(new JObject { ["persisted"] = true, ["saved"] = true, ["writeAttempted"] = true });

            Assert.Contains("genexus_lifecycle", Tools(Follow(args, payload)));
        }

        [Theory]
        [InlineData("false")]
        [InlineData("yes")]
        [InlineData("")]
        public void AnAmbiguousDryRunSpelling_IsNotAPreview(string spelling)
        {
            // IsEffectFreeDryRunPreview treats anything but an unambiguous true as a write.
            var args = EditArgs();
            args["dryRun"] = spelling;
            var payload = Envelope(new JObject { ["persisted"] = true, ["saved"] = true });

            Assert.Contains("genexus_lifecycle", Tools(Follow(args, payload)));
        }

        [Fact]
        public void BlockedPreview_NeverSuggestsApplyBuildPreviewUndoOrReorg()
        {
            var payload = Envelope(new JObject
            {
                ["persisted"] = false, ["saved"] = false, ["patchMatch"] = true, ["isolationEligible"] = false,
                ["canApply"] = false, ["writeBlocker"] = "ObjectSaveIsolationUnverified"
            });

            var result = NextLegalActionsBuilder.BuildFor("genexus_edit", EditArgs(true), payload, isError: false);

            Assert.Null(result);
        }

        [Fact]
        public void ResponseThatReportsABlockerIsNotFollowedUpEvenWhenTheRequestDidNotSayDryRun()
        {
            // The blocker is evidence in the response; it must not depend on how the caller spelled the request.
            var payload = Envelope(new JObject { ["canApply"] = false, ["writeBlocker"] = "ObjectSaveIsolationUnverified" });

            var result = NextLegalActionsBuilder.BuildFor("genexus_edit", EditArgs(), payload, isError: false);

            Assert.Null(result);
        }

        [Theory]
        [InlineData("queued")]
        [InlineData("running")]
        public void AcceptedAsynchronousEdit_PointsAtItsOwnStatusPoll_NotAtBuild(string state)
        {
            var payload = new JObject
            {
                ["job_id"] = "op-123", ["operationId"] = "op-123", ["status"] = state, ["pollTarget"] = "op:op-123",
                ["hint"] = "Edit accepted; poll genexus_lifecycle(action='status'|'result', target='op:op-123')"
            };

            var result = NextLegalActionsBuilder.BuildFor("genexus_edit", EditArgs(false), payload, isError: false);

            Assert.NotNull(result);
            var only = Assert.Single(result!);
            Assert.Equal("genexus_lifecycle", only["tool"]?.ToString());
            Assert.Equal("status", only["args"]?["action"]?.ToString());
            Assert.Equal("op:op-123", only["args"]?["target"]?.ToString());
            Assert.DoesNotContain("build", result.Select(s => s["args"]?["action"]?.ToString()));
        }

        [Fact]
        public void AppliedEdit_KeepsTheBuildPreviewAndUndoFollowUps()
        {
            var payload = Envelope(new JObject { ["persisted"] = true, ["saved"] = true, ["verified"] = true, ["writeAttempted"] = true });

            var result = NextLegalActionsBuilder.BuildFor("genexus_edit", EditArgs(false), payload, isError: false);

            var tools = Tools(result);
            Assert.Contains("genexus_lifecycle", tools);
            Assert.Contains("genexus_browser", tools);
            Assert.Contains("genexus_versioning", tools);
        }

        [Fact]
        public void EditWithoutAnyEvidenceOfAPreview_KeepsTheLegacyFollowUps()
        {
            // Backward compatibility: a flat success payload with no preview markers behaves as before.
            var args = new JObject { ["name"] = "Foo", ["part"] = "Events" };
            var payload = new JObject { ["status"] = "Success", ["name"] = "Foo" };

            var result = NextLegalActionsBuilder.BuildFor("genexus_edit", args, payload, isError: false);

            Assert.Contains("genexus_lifecycle", Tools(result));
        }
    }
}
