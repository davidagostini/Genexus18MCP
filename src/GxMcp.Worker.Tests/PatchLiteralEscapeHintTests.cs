using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // A client that sends the two characters backslash and n where a line break was meant gets a patch that
    // matches nothing. The MCP must not "fix" the text, and the miss must say why it happened.
    public sealed class PatchLiteralEscapeHintTests
    {
        private const string Source = "Event Start\n\t// anchor\nEndEvent\n";

        [Theory]
        [InlineData("Event Start\\n\\t// anchor", "\\n,\\t")]
        [InlineData("a\\rb", "\\r")]
        [InlineData("only\\nthis", "\\n")]
        public void EscapedSequencesTheSourceDoesNotContain_AreNamed(string patchText, string expected)
            => Assert.Equal(expected.Split(','), PatchService.LiteralEscapeSequences(patchText, Source));

        [Fact]
        public void RealLineBreaksAndTabs_AreNotFlagged()
            => Assert.Empty(PatchService.LiteralEscapeSequences("Event Start\n\t// anchor", Source));

        [Fact]
        public void ASequenceTheSourceItselfContains_IsNotFlagged()
        {
            // A source with a string such as "a\nb" legitimately holds the two characters.
            Assert.Empty(PatchService.LiteralEscapeSequences("Msg('a\\nb')", "Msg('a\\nb')\n"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void NoPatchText_IsNotFlagged(string patchText)
            => Assert.Empty(PatchService.LiteralEscapeSequences(patchText, Source));

        [Fact]
        public void TheMiss_CarriesTheSequencesAndAHintAndNothingElseChanges()
        {
            string failure = new JObject { ["status"] = "error", ["code"] = "NoMatch", ["message"] = "Context block not found." }.ToString();

            var json = JObject.Parse(PatchService.AttachLiteralEscapeHint(failure, "Event Start\\n\\t// anchor", Source));

            Assert.Equal("NoMatch", json["code"].ToString());
            Assert.Equal("Context block not found.", json["message"].ToString());
            Assert.Equal(new[] { "\\n", "\\t" }, json["literalEscapeSequences"].ToObject<string[]>());
            string hint = json["literalEscapeHint"].ToString();
            Assert.Contains("does not convert escape sequences", hint);
            Assert.Contains("Nothing was written", hint);
        }

        [Fact]
        public void TheHintGoesInsideTheErrorNodeWhenThereIsOne()
        {
            string failure = new JObject { ["status"] = "error", ["error"] = new JObject { ["code"] = "NoMatch" } }.ToString();

            var json = JObject.Parse(PatchService.AttachLiteralEscapeHint(failure, "a\\nb", Source));

            Assert.NotNull(json["error"]["literalEscapeHint"]);
            Assert.Null(json["literalEscapeHint"]);
        }

        [Fact]
        public void NothingToReport_ReturnsTheFailureUntouched()
        {
            string failure = new JObject { ["code"] = "NoMatch" }.ToString();

            Assert.Equal(failure, PatchService.AttachLiteralEscapeHint(failure, "plain text", Source));
            Assert.Equal("not json", PatchService.AttachLiteralEscapeHint("not json", "a\\nb", Source));
        }
    }
}
