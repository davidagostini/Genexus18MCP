using System;
using GxMcp.TestSupport;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The variables of a batch are validated and built detached and are added only when every item passed;
    // a batch that fails answers before anything is saved, and object_atomic compensates. PopulateVariablesInto,
    // AddVariablesInternal and the object_atomic variables step need a KB object with a Variables part, so no
    // unit test can run them: the order in which they decide is pinned on the source here, and the parts that
    // run without a KB are exercised in VariableBatchSupportTests.
    public class VariableBatchAtomicityGuardTests
    {
        private static string Source(params string[] file)
        {
            return SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(file));
        }

        private static string VariablesSource()
        {
            return Source("src", "GxMcp.Worker", "Services", "WriteService.Variables.cs");
        }

        // The overload of PopulateVariablesInto that has a body; the three before it only forward to it.
        private static string PopulateBody()
        {
            return SourceAssert.MethodBody(VariablesSource(),
                "out System.Collections.Generic.List<ExpectedObjectBinding> objectBound,\n            out System.Collections.Generic.List<string> addedNames)");
        }

        private static string AddVariablesBody()
        {
            return SourceAssert.MethodBody(VariablesSource(), "private string AddVariablesInternal(string target, JArray variables)");
        }

        private static int At(string text, string needle, int from = 0)
        {
            int at = text.IndexOf(needle, from, StringComparison.Ordinal);
            Assert.True(at >= 0, "not found: " + needle);
            return at;
        }

        [Fact]
        public void TheStagedVariablesAreCommittedOnlyThroughTheUndoingCommit()
        {
            string body = PopulateBody();

            Assert.Equal(1, SourceAssert.Count(body, "VariableBatchSupport.TryAddAll(stagedVariables,"));
            // A bare loop over the staged items leaves the earlier ones in the live part when a later one throws.
            Assert.DoesNotContain("foreach (var variable in stagedVariables)", body);
            Assert.Equal(1, SourceAssert.Count(body, "varPart.Variables.Add("));
        }

        [Fact]
        public void TheCommitRunsOnlyWhenNoItemFailed_AndARefusalIsDiscardedLikeAFailedItem()
        {
            string body = PopulateBody();

            int commit = At(body, "if (failed == 0 && !VariableBatchSupport.TryAddAll(");
            int discard = At(body, "if (failed > 0)");
            Assert.True(commit < discard, "a refused commit has to reach the discard of a failed batch");

            // Between the two the refusal counts as a failed item and says which one it was.
            string refusal = body.Substring(commit, discard - commit);
            Assert.Contains("failed++;", refusal);
            Assert.Contains("VariableBatchSupport.MarkRefused(outcomes, stagedVariables[refusedIndex].Name,", refusal);

            // The discard leaves nothing added and ends the batch.
            string discarded = body.Substring(discard);
            Assert.Contains("outcome[\"status\"] = \"NotAdded\";", discarded);
            Assert.Contains("added = 0;", discarded);
            Assert.Contains("return;", discarded);
        }

        [Fact]
        public void AReservedStandardNameFailsItsItemBeforeAnythingIsBuilt()
        {
            string body = PopulateBody();

            int exists = At(body, "[\"itemStatus\"] = \"Exists\"");
            int refusal = At(body, "VariableBatchSupport.IsSdkStandardName(vName)");
            int resolution = At(body, "VariableTypeResolver.Resolve(vType)");
            Assert.True(exists < refusal && refusal < resolution, "refused after the Exists check, before the type is resolved");

            string refused = body.Substring(refusal, resolution - refusal);
            Assert.Contains("failed++;", refused);
            Assert.Contains("[\"reason\"] = \"StandardVariableName\"", refused);
            Assert.Contains("continue;", refused);
        }

        [Fact]
        public void AFailedBatchAnswersWithTheOutcomesBeforeAnythingIsSaved()
        {
            string body = AddVariablesBody();

            int populate = At(body, "PopulateVariablesInto(");
            int failedAnswer = At(body, "if (failed > 0)", populate);
            int save = At(body, "ForceSaveVariableOwner(obj)");
            Assert.True(failedAnswer < save, "a failed batch has to answer before the owner is saved");

            string answer = body.Substring(failedAnswer, save - failedAnswer);
            Assert.Contains("return McpResponse.Err(code: \"VariableBatchValidationFailed\"", answer);
            Assert.Contains("[\"outcomes\"] = outcomes", answer);
            Assert.Contains("[\"saved\"] = false", answer);
        }

        [Fact]
        public void ATargetThatCannotBeResolvedIsAnErrorNotAnEmptySuccess()
        {
            string body = AddVariablesBody();

            int resolve = At(body, "ResolveVariableTarget(target, ref scratch, out var obj, out var varPart, out _);");
            int populate = At(body, "PopulateVariablesInto(");
            Assert.Contains("if (err != null) return err;", body.Substring(resolve, populate - resolve));
        }

        [Fact]
        public void ObjectAtomicCompensatesWhenTheVariablesPhaseFails()
        {
            string run = SourceAssert.MethodBody(
                Source("src", "GxMcp.Worker", "Services", "AtomicCreateService.cs"), "public string Run(JObject args)");

            int populate = At(run, "_writeService.PopulateVariablesInto(");
            int describe = At(run, "steps[\"variables\"] = DescribeVariablePhase(added, existed, failed, outcomes);", populate);
            int failure = At(run, "if ((string)steps[\"variables\"][\"status\"] == \"error\")", describe);
            int compensate = At(run,
                "return CompensateAndFail(spec, exists, (JObject)steps[\"variables\"], \"variables\", touchedParts);", failure);
            // The compensation belongs to the variables phase: it comes before the next phase starts.
            Assert.True(compensate < At(run, "currentField = \"rules\";"), "compensate before the rules phase");
        }
    }
}
