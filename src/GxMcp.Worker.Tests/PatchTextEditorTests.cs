using Newtonsoft.Json.Linq;
using Xunit;
using Services = GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public class PatchTextEditorTests
    {
        [Fact]
        public void TryReplace_ExactSingleMatch_ReplacesRequestedBlock()
        {
            string result = Services.PatchTextEditor.TryReplace(
                new[] { "before", "old one", "old two", "after" },
                new[] { "old one", "old two" },
                "new one\nnew two",
                1,
                out string status,
                out _,
                out int count);

            Assert.Equal("Applied", status);
            Assert.Equal(1, count);
            Assert.Equal("before\nnew one\nnew two\nafter", result);
        }

        [Fact]
        public void TryReplace_CompletePartWithEmptyContent_ReturnsEmptyResult()
        {
            string result = Services.PatchTextEditor.TryReplace(
                new[] { "old" }, new[] { "old" }, string.Empty, 1,
                out string status, out _, out int count);

            Assert.Equal("Applied", status);
            Assert.Equal(1, count);
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void TryReplace_AmbiguousExactMatch_DoesNotProduceContent()
        {
            string result = Services.PatchTextEditor.TryReplace(
                new[] { "same", "same" }, new[] { "same" }, "new", 1,
                out string status, out string details, out int count);

            Assert.Equal("Ambiguous", status);
            Assert.Equal(2, count);
            Assert.Equal(string.Empty, result);
            Assert.Contains("replaceAll=true", details);
        }

        [Fact]
        public void TryReplace_ReplaceAll_ReplacesEveryExactOccurrence()
        {
            string result = Services.PatchTextEditor.TryReplace(
                new[] { "same", "middle", "same" }, new[] { "same" }, "new", 1,
                out string status, out _, out int count, replaceAll: true);

            Assert.Equal("Applied", status);
            Assert.Equal(2, count);
            Assert.Equal("new\nmiddle\nnew", result);
        }

        [Fact]
        public void TryReplace_FuzzyWhitespaceMatch_PreservesSurroundingLines()
        {
            string result = Services.PatchTextEditor.TryReplace(
                new[] { "before", "  if   value", "  endif", "after" },
                new[] { "if value", "endif" },
                "replacement",
                1,
                out string status,
                out _,
                out int count);

            Assert.Equal("Applied", status);
            Assert.Equal(1, count);
            Assert.Equal("before\nreplacement\nafter", result);
        }

        [Fact]
        public void TryInsertAfter_ExactAnchor_InsertsAfterCompleteBlock()
        {
            string result = Services.PatchTextEditor.TryInsertAfter(
                new[] { "one", "anchor", "two" }, new[] { "anchor" }, "inserted", 1,
                out string status, out _, out int count);

            Assert.Equal("Applied", status);
            Assert.Equal(1, count);
            Assert.Equal("one\nanchor\ninserted\ntwo", result);
        }

        [Fact]
        public void FindNearMatches_ReturnsHighestSimilarityFirst()
        {
            var matches = Services.PatchTextEditor.FindNearMatches(
                new[] { "alpha", "beta", "noise", "alpha", "other" },
                new[] { "alpha", "beta" },
                3);

            Assert.NotEmpty(matches);
            Assert.Equal(0, matches[0].StartLine);
            Assert.Equal(1d, matches[0].Similarity);
        }

        [Fact]
        public void Receipt_EmptyReplacement_ProvesOldContextAbsent()
        {
            var payload = new JObject();
            var verification = Services.TextPersistenceVerifier.Evaluate(
                requested: string.Empty,
                persisted: string.Empty,
                requestedMode: "exact",
                partName: "Source");

            bool verified = Services.PatchPersistenceReceipt.AttachVerification(
                payload,
                verification,
                requestedReplacement: string.Empty,
                originalContext: "old",
                savedSource: string.Empty,
                persistedSource: string.Empty,
                verifyMode: "exact",
                partName: "Source",
                matchCount: 1);

            Assert.True(verified);
            Assert.Equal(0, payload["persistedMatchCount"]?.Value<int>());
            Assert.False(payload["oldContentPresent"]?.Value<bool>());
            Assert.False(payload["verification"]?["replacementPresent"] == null);
            Assert.True(payload["verification"]?["replacementPresent"]?.Value<bool>());
        }

        [Fact]
        public void Receipt_OldContextStillPresent_IsReported()
        {
            var payload = new JObject();
            var verification = Services.TextPersistenceVerifier.Evaluate(
                requested: "old",
                persisted: "old",
                requestedMode: "exact",
                partName: "Source");

            bool verified = Services.PatchPersistenceReceipt.AttachVerification(
                payload, verification, "old", "old", "old", "old", "exact", "Source", 1);

            Assert.True(verified);
            Assert.Equal(1, payload["persistedMatchCount"]?.Value<int>());
            Assert.True(payload["oldContentPresent"]?.Value<bool>());
        }

        [Fact]
        public void Receipt_MarkVerified_RemovesContradictoryLegacyDiagnostics()
        {
            var payload = new JObject
            {
                ["error"] = "legacy mismatch",
                ["mutation"] = new JObject(),
                ["verificationWarning"] = "legacy warning"
            };

            Services.PatchPersistenceReceipt.MarkVerified(payload, saved: true);

            Assert.Null(payload["error"]);
            Assert.Null(payload["mutation"]);
            Assert.Null(payload["verificationWarning"]);
            Assert.Equal("Applied", payload["code"]?.ToString());
            Assert.True(payload["saved"]?.Value<bool>());
            Assert.True(payload["verified"]?.Value<bool>());
        }

        [Fact]
        public void Receipt_MarkNotPersisted_SeparatesSaveFromVerification()
        {
            var payload = new JObject();

            Services.PatchPersistenceReceipt.MarkNotPersisted(payload, saved: true, verifyError: "different");

            Assert.Equal("WriteNotPersisted", payload["code"]?.ToString());
            Assert.True(payload["saved"]?.Value<bool>());
            Assert.False(payload["verified"]?.Value<bool>());
            Assert.Equal("different", payload["persistedVerifyError"]?.ToString());
        }

        [Fact]
        public void Receipt_RollbackIncludesHashesAndVerificationState()
        {
            var verification = Services.TextPersistenceVerifier.Evaluate("snapshot", "snapshot", "exact", "Source");

            JObject rollback = Services.PatchPersistenceReceipt.BuildRollback(true, verification, null);

            Assert.True(rollback["saved"]?.Value<bool>());
            Assert.True(rollback["verified"]?.Value<bool>());
            Assert.False(string.IsNullOrWhiteSpace(rollback["requestedHash"]?.ToString()));
            Assert.Equal(rollback["requestedHash"]?.ToString(), rollback["persistedHash"]?.ToString());
            Assert.Equal(JTokenType.Null, rollback["error"]?.Type);
        }

        [Fact]
        public void Receipt_DivergentFreshRead_DoesNotClaimConfirmation()
        {
            var payload = new JObject();
            var verification = Services.TextPersistenceVerifier.Evaluate("new", "old", "exact", "Source");

            bool verified = Services.PatchPersistenceReceipt.AttachVerification(
                payload, verification, "new", "old", "new", "old", "exact", "Source", 1);

            Assert.False(verified);
            Assert.False(payload["reReadConfirmed"]?.Value<bool>());
            Assert.False(payload["verification"]?["reReadConfirmed"]?.Value<bool>());
            Assert.True(payload["verification"]?["readCompleted"]?.Value<bool>());
            Assert.Equal("fresh-sdk-read", payload["verification"]?["source"]?.ToString());
        }
    }
}
