using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ObjectMoveSnapshotTests
    {
        [Fact]
        public void FindChangedPartKeys_ExactSnapshot_HasNoDifferences()
        {
            var expected = Parts(("source", "a"), ("rules", "b"), ("variables", "c"));
            var actual = Parts(("variables", "c"), ("rules", "b"), ("source", "a"));

            Assert.Empty(ObjectMoveSnapshot.FindChangedPartKeys(expected, actual));
        }

        [Fact]
        public void FindChangedPartKeys_ReportsChangedMissingAndUnexpectedParts()
        {
            var expected = Parts(("source", "before"), ("rules", "parm"), ("variables", "vars"));
            var actual = Parts(("source", "after"), ("rules", "parm"), ("documentation", "doc"));

            Assert.Equal(
                new[] { "documentation", "source", "variables" },
                ObjectMoveSnapshot.FindChangedPartKeys(expected, actual));
        }

        [Fact]
        public void FindChangedPartKeys_IsCaseInsensitiveForPartIdentity()
        {
            var expected = Parts(("SOURCE", "same"));
            var actual = Parts(("source", "same"));

            Assert.Empty(ObjectMoveSnapshot.FindChangedPartKeys(expected, actual));
        }

        [Fact]
        public void NormalizeObjectXml_IgnoresPlacementAndVersionButPreservesAuthoredProperties()
        {
            const string before = "<Object Parent='Root' LastUpdate='1'><Properties><Description>Keep</Description></Properties><Module>Root</Module></Object>";
            const string after = "<Object Parent='operacional' LastUpdate='2'><Properties><Description>Keep</Description></Properties><Module>operacional</Module></Object>";
            const string changed = "<Object Parent='operacional' LastUpdate='2'><Properties><Description>Changed</Description></Properties><Module>operacional</Module></Object>";

            Assert.Equal(ObjectMoveSnapshot.NormalizeObjectXml(before), ObjectMoveSnapshot.NormalizeObjectXml(after));
            Assert.NotEqual(ObjectMoveSnapshot.NormalizeObjectXml(before), ObjectMoveSnapshot.NormalizeObjectXml(changed));
        }

        [Fact]
        public void NormalizeObjectXml_PreservesRepeatedAndPlacementNamedAuthoredProperties()
        {
            const string before = "<Object Parent='Root'><Properties><Item>A</Item><Item>B</Item><Module>Authored</Module></Properties></Object>";
            const string changedSecond = "<Object Parent='operacional'><Properties><Item>A</Item><Item>C</Item><Module>Authored</Module></Properties></Object>";
            const string changedModuleProperty = "<Object Parent='operacional'><Properties><Item>A</Item><Item>B</Item><Module>Changed</Module></Properties></Object>";

            Assert.NotEqual(ObjectMoveSnapshot.NormalizeObjectXml(before), ObjectMoveSnapshot.NormalizeObjectXml(changedSecond));
            Assert.NotEqual(ObjectMoveSnapshot.NormalizeObjectXml(before), ObjectMoveSnapshot.NormalizeObjectXml(changedModuleProperty));
        }

        [Fact]
        public void NormalizeObjectXml_IgnoresGenericPlacementPropertyButKeepsOtherGenericProperties()
        {
            const string before = "<Object><Properties><Property><Name>Module</Name><Value>Root</Value></Property><Property><Name>Description</Name><Value>Keep</Value></Property></Properties></Object>";
            const string moved = "<Object><Properties><Property><Name>Module</Name><Value>operacional</Value></Property><Property><Name>Description</Name><Value>Keep</Value></Property></Properties></Object>";
            const string authoredChange = "<Object><Properties><Property><Name>Module</Name><Value>operacional</Value></Property><Property><Name>Description</Name><Value>Changed</Value></Property></Properties></Object>";

            Assert.Equal(ObjectMoveSnapshot.NormalizeObjectXml(before), ObjectMoveSnapshot.NormalizeObjectXml(moved));
            Assert.NotEqual(ObjectMoveSnapshot.NormalizeObjectXml(before), ObjectMoveSnapshot.NormalizeObjectXml(authoredChange));
        }

        private static Dictionary<string, byte[]> Parts(params (string key, string value)[] values)
        {
            var result = new Dictionary<string, byte[]>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
                result[value.key] = System.Text.Encoding.UTF8.GetBytes(value.value);
            return result;
        }
    }
}
