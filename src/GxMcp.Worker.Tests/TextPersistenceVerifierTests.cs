using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class TextPersistenceVerifierTests
    {
        [Fact]
        public void Normalized_CrlfRequestedAndLfPersisted_MatchWithEvidence()
        {
            var result = TextPersistenceVerifier.Evaluate(
                "For Each\r\n    Do 'Work'\r\nEndFor\r\n",
                "For Each\n    Do 'Work'\nEndFor\n",
                "normalized",
                "Source");

            Assert.True(result.Matches);
            Assert.Contains("EOL", result.NormalizationApplied);
            Assert.NotEqual(result.RequestedHash, result.PersistedHash);
            Assert.Equal(result.NormalizedRequestedHash, result.NormalizedPersistedHash);
        }

        [Fact]
        public void Normalized_AdditionalBlankLineAndTrailingSpaces_Match()
        {
            var result = TextPersistenceVerifier.Evaluate(
                "If &Ready\r\n\r\n    Do 'Work'   \r\nEndIf",
                "If &Ready\n\n\n    Do 'Work'\nEndIf",
                null,
                "Source");

            Assert.True(result.Matches);
            Assert.Contains("blank-lines", result.NormalizationApplied);
        }

        [Fact]
        public void Normalized_SdkInsertedSingleBlankLine_Matches()
        {
            var result = TextPersistenceVerifier.Evaluate(
                "If &Ready\n    Do 'Work'\nEndIf",
                "If &Ready\n\n    Do 'Work'\nEndIf",
                "normalized",
                "Source");

            Assert.True(result.Matches);
            Assert.Contains("blank-lines", result.NormalizationApplied);
        }

        [Fact]
        public void Normalized_MissingReplacement_DoesNotMatch()
        {
            var result = TextPersistenceVerifier.Evaluate(
                "If &Ready\n    Do 'NewWork'\nEndIf",
                "If &Ready\n    Do 'OldWork'\nEndIf",
                "normalized",
                "Source");

            Assert.False(result.Matches);
            Assert.NotNull(result.DiffNormalized);
            Assert.NotEqual(result.NormalizedRequestedHash, result.NormalizedPersistedHash);
        }

        [Fact]
        public void Exact_SourceAcceptsSdkEolRenderingButPreservesRawHashes()
        {
            var result = TextPersistenceVerifier.Evaluate("A\r\nB", "A\nB", "exact", "Rules");
            Assert.True(result.Matches);
            Assert.NotEqual(result.RequestedHash, result.PersistedHash);
            Assert.Contains("EOL", result.NormalizationApplied);
        }

        [Fact]
        public void Exact_NonSourcePartStillRejectsEolDifference()
        {
            var result = TextPersistenceVerifier.Evaluate("A\r\nB", "A\nB", "exact", "Structure");
            Assert.False(result.Matches);
        }

        [Fact]
        public void Exact_SourceStillRejectsCommentLoss()
        {
            var result = TextPersistenceVerifier.Evaluate(
                "//msg(\"comment-only\",nowait)",
                "msg(\"comment-only\",nowait)",
                "exact",
                "Source");
            Assert.False(result.Matches);
        }

        [Fact]
        public void Semantic_ToleratesSdkSpacingAndKeywordCasingButPreservesStrings()
        {
            var equivalent = TextPersistenceVerifier.Evaluate(
                "IF &Ready\n Msg('Keep Case')\nENDIF",
                "if   &ready\nmsg( 'Keep Case' )\nendif",
                "semantic",
                "Source");
            var changedLiteral = TextPersistenceVerifier.Evaluate(
                "Msg('Keep Case')",
                "msg('keep case')",
                "semantic",
                "Source");

            Assert.True(equivalent.Matches);
            Assert.False(changedLiteral.Matches);
        }

        [Fact]
        public void SourceRulesEventsConditions_DefaultToNormalized()
        {
            Assert.Equal("normalized", TextPersistenceVerifier.ResolveMode(null, "Source"));
            Assert.Equal("normalized", TextPersistenceVerifier.ResolveMode(null, "Rules"));
            Assert.Equal("normalized", TextPersistenceVerifier.ResolveMode(null, "Events"));
            Assert.Equal("normalized", TextPersistenceVerifier.ResolveMode(null, "Conditions"));
            Assert.Equal("exact", TextPersistenceVerifier.ResolveMode(null, "Structure"));
        }

        [Fact]
        public void Exact_EventsAcceptsSdkEolRendering()
        {
            var result = TextPersistenceVerifier.Evaluate("Event Start\r\nEndEvent\r\n", "Event Start\nEndEvent\n", "exact", "Events");
            Assert.True(result.Matches);
            Assert.Contains("EOL", result.NormalizationApplied);
        }

        [Fact]
        public void Normalized_EventsWithBlankLineDifferences_Matches()
        {
            var result = TextPersistenceVerifier.Evaluate(
                "Event 'DoSomething'\r\n    msg('Hi')\r\nEndEvent",
                "Event 'DoSomething'\n\n    msg('Hi')\nEndEvent",
                null,
                "Events");
            Assert.True(result.Matches);
        }
    }
}
