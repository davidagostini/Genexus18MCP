using System;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class CompareContentDiffTests
    {
        public class TextPart { public string Source => "body"; }
        public class BrokenPart { public string Source => throw new InvalidOperationException(); }
        public class StructuredPart { public object Source => new object(); }

        [Fact]
        public void NativeReaderFallbackOnlyTreatsStringSourceAsText()
        {
            Assert.Equal("body", CompareService.SourceReader(new TextPart())());
            Assert.Null(CompareService.SourceReader(new StructuredPart()));
            var names = new JArray(); var diffs = new JArray();
            CompareService.AppendPartDifference(names, diffs, "Rules", "Rules", () => false,
                CompareService.SourceReader(new BrokenPart()), CompareService.SourceReader(new TextPart()));
            Assert.Equal("readFailed", diffs[0]["omittedReason"].Value<string>());
        }

        [Theory]
        [InlineData("Source")]
        [InlineData("Events")]
        [InlineData("Rules")]
        public void DifferentTextPreservesDescriptorAndAddsUnifiedEvidence(string part)
        {
            var names = new JArray();
            var diffs = new JArray();
            CompareService.AppendPartDifference(names, diffs, "SDK" + part, part,
                () => false, () => "keep\nold\n", () => "keep\nnew\n");
            Assert.Equal("SDK" + part, names[0].Value<string>());
            Assert.Equal(part, diffs[0]["part"].Value<string>());
            Assert.Equal("SDK" + part, diffs[0]["partType"].Value<string>());
            Assert.Equal("--- a/part\n+++ b/part\n@@ -1,2 +1,2 @@\n keep\n-old\n+new\n",
                diffs[0]["unified"].Value<string>());
        }

        [Fact]
        public void EqualPartDoesNotReadOrProduceDiff()
        {
            var names = new JArray(); var diffs = new JArray();
            CompareService.AppendPartDifference(names, diffs, "Source", "Source", () => true,
                () => throw new Exception(), () => throw new Exception());
            Assert.Empty(names); Assert.Empty(diffs);
        }

        [Fact]
        public void PerPartFailuresDoNotLoseOtherEvidence()
        {
            var names = new JArray(); var diffs = new JArray();
            CompareService.AppendPartDifference(names, diffs, "Layout", "Layout", () => false, null, null);
            CompareService.AppendPartDifference(names, diffs, "Rules", "Rules", () => false,
                () => throw new Exception("private detail"), () => "b");
            CompareService.AppendPartDifference(names, diffs, "Variables", "Variables",
                () => throw new Exception(), null, null);
            CompareService.AppendPartDifference(names, diffs, "Source", "Source", () => false, () => "a", () => "b");
            Assert.Equal(3, names.Count);
            Assert.Equal("nonTextualPart", diffs[0]["omittedReason"].Value<string>());
            Assert.Equal("readFailed", diffs[1]["omittedReason"].Value<string>());
            Assert.Equal("compareFailed", diffs[2]["omittedReason"].Value<string>());
            Assert.Contains("+b\n", diffs[3]["unified"].Value<string>());
            Assert.DoesNotContain("private detail", diffs.ToString());
        }

        [Theory]
        [InlineData("a\r\nb\r\n", "a\nb\n")]
        [InlineData("a\rb\r", "a\nb\n")]
        [InlineData("", "")]
        public void NormalizedEqualityIsExplicitWithoutSpuriousHunk(string a, string b)
        {
            var result = CompareService.BuildTextDiff(a, b);
            Assert.Equal("normalizedTextEqual", result["omittedReason"].Value<string>());
            Assert.Null(result["unified"]);
        }

        [Theory]
        [InlineData("", "a\n", "@@ -0,0 +1,1 @@\n+a\n")]
        [InlineData("a\n", "", "@@ -1,1 +0,0 @@\n-a\n")]
        [InlineData("a", "a\n", "@@ -1,1 +1,1 @@\n-a\n\\ No newline at end of file\n+a\n")]
        [InlineData("a\n", "a", "@@ -1,1 +1,1 @@\n-a\n+a\n\\ No newline at end of file\n")]
        public void UnifiedRangesAndFinalNewlinesAreAccurate(string a, string b, string expected)
        {
            Assert.Equal("--- a/part\n+++ b/part\n" + expected,
                CompareService.BuildTextDiff(a, b)["unified"].Value<string>());
        }

        [Fact]
        public void LimitsOmitIncompleteHunksAndBoundManyShortLines()
        {
            var oversized = CompareService.BuildTextDiff(new string('a', CompareService.MaxSourceChars + 1), "b");
            Assert.Equal("truncated", oversized["omittedReason"].Value<string>());
            Assert.Null(oversized["unified"]);
            var expanded = CompareService.BuildTextDiff(new string('a', 600000), new string('b', 600000));
            Assert.True(expanded["truncated"].Value<bool>());
            Assert.Null(expanded["unified"]);
            var manyLines = new string('\n', 100000);
            var sparse = CompareService.BuildTextDiff(manyLines + "a\n", manyLines + "b\n");
            Assert.Contains("@@ -99998,4 +99998,4 @@", sparse["unified"].Value<string>());
            Assert.True(sparse["unified"].Value<string>().Length < 200);
        }

        [Fact]
        public void EscapedMultipartDiffsStayBelowGatewayAndSharedHostLimits()
        {
            var names = new JArray(); var diffs = new JArray();
            for (int i = 0; i < 16; i++)
                CompareService.AppendPartDifference(names, diffs, "Text" + i, "Text" + i,
                    () => false, () => new string('\u0001', 8000), () => new string('\u0002', 8000));
            var result = new JObject { ["equal"] = false, ["differences"] = names, ["diffs"] = diffs };
            Assert.Equal(16, names.Count);
            Assert.NotNull(diffs[0]["unified"]);
            Assert.Equal("totalDiffBytes", diffs[1]["limit"].Value<string>());
            Assert.Null(diffs[1]["maxChars"]);
            Assert.Equal(CompareService.MaxTotalDiffBytes, diffs[1]["maxBytes"].Value<int>());
            Assert.Null(diffs[1]["unified"]);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.ToString()) < 150000);
        }

        [Theory]
        [InlineData("Procedure", "c5f0ef88-9ef8-4218-bf76-915024b3c48f", "Procedure", "Source")]
        [InlineData("DataProvider", "91705646-6086-4f32-8871-08149817e754", "DataProvider", "Source")]
        [InlineData("WebPanel", "c44bd5ff-f918-415b-98e6-aca44fed84fa", "Events", "Events")]
        [InlineData("Transaction", "9b0a32a3-de6d-4be1-a4dd-1b85d3741534", "Rules", "Rules")]
        [InlineData("SDPanel", "163f0d8b-d8ac-4db4-8dd4-de8979f2b5b9", "SDConditions", "Conditions")]
        [InlineData("DesignSystem", "75e52d99-6edd-4bad-a1d7-dcc9b7f000ef", "DesignTokens", "Tokens")]
        [InlineData("DesignSystem", "c6b14574-4f5f-4e35-aaa7-e322e88a9a10", "DesignStyles", "Styles")]
        public void AliasUsesSamePartIdentityAsRead(string type, string id, string descriptor, string alias)
        {
            Assert.Equal(alias, CompareService.ResolveReadPart(type, Guid.Parse(id), descriptor));
        }
    }
}
