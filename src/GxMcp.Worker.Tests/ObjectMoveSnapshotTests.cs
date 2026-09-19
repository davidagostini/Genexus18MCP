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

        // ---- issue #238: placement-aware diff for Folder-destination property echoes ----

        // Canonical form of <Object><Properties><Property><Name>Description</Name>
        // <Value>Keep</Value></Property><Property><Name>WebFolder</Name><Value>Root</Value>
        // </Property></Properties></Object> before/after a move into folder 'operacional'.
        private const string PropertyEchoBefore =
            "Object[1]/Guid[1]=42\n" +
            "Object[1]/Name[1]=TempProc\n" +
            "Object[1]/Properties[1]/Property[1]/Name[1]=Description\n" +
            "Object[1]/Properties[1]/Property[1]/Value[1]=Keep\n" +
            "Object[1]/Properties[1]/Property[2]/Name[1]=WebFolder\n" +
            "Object[1]/Properties[1]/Property[2]/Value[1]=Root Module\n";

        private const string PropertyEchoAfterFolderMove =
            "Object[1]/Guid[1]=42\n" +
            "Object[1]/Name[1]=TempProc\n" +
            "Object[1]/Properties[1]/Property[1]/Name[1]=Description\n" +
            "Object[1]/Properties[1]/Property[1]/Value[1]=Keep\n" +
            "Object[1]/Properties[1]/Property[2]/Name[1]=WebFolder\n" +
            "Object[1]/Properties[1]/Property[2]/Value[1]=operacional\n";

        [Fact]
        public void DiffObjectXml_PlacementEchoOnGenericProperty_IsTolerated()
        {
            // The exact issue #238 signature: only Property[2]/Value[1] changed, and the new
            // value equals the destination folder name.
            var paths = ObjectMoveSnapshot.DiffObjectXml(PropertyEchoBefore, PropertyEchoAfterFolderMove, "operacional");
            Assert.Empty(paths);
        }

        [Fact]
        public void DiffObjectXml_ValueChangeNotMatchingPlacement_StillFails()
        {
            string after = PropertyEchoAfterFolderMove.Replace("=operacional", "=outraPasta");
            var paths = ObjectMoveSnapshot.DiffObjectXml(PropertyEchoBefore, after, "operacional");
            Assert.Contains("Object[1]/Properties[1]/Property[2]/Value[1]", paths);
        }

        [Fact]
        public void DiffObjectXml_WithoutPlacementName_IsStrict()
        {
            // Legacy behaviour: without a destination the same XML divergence is reported.
            var paths = ObjectMoveSnapshot.DiffObjectXml(PropertyEchoBefore, PropertyEchoAfterFolderMove, null);
            Assert.Contains("Object[1]/Properties[1]/Property[2]/Value[1]", paths);
        }

        [Fact]
        public void DiffObjectXml_PropertyKeyAddedOrRemoved_NeverTolerated()
        {
            // A new property appearing is authored content even if its value echoes the
            // folder. Its Value[1] line would satisfy the echo check, so the guard must
            // still reject it: the Value exists on only ONE side (no before value).
            const string after = PropertyEchoAfterFolderMove +
                "Object[1]/Properties[1]/Property[3]/Name[1]=NewProp\n" +
                "Object[1]/Properties[1]/Property[3]/Value[1]=operacional\n";
            var paths = ObjectMoveSnapshot.DiffObjectXml(PropertyEchoBefore, after, "operacional");
            Assert.Contains("Object[1]/Properties[1]/Property[3]/Name[1]", paths);
            Assert.Contains("Object[1]/Properties[1]/Property[3]/Value[1]", paths);
        }

        [Fact]
        public void DiffObjectXml_FolderPlacementGuidRewrite_IsTolerated()
        {
            string after = PropertyEchoBefore.Replace(
                "=Root Module",
                "=00000000-0000-0000-0000-000000000008");
            string before = PropertyEchoBefore.Replace("=Root Module", "=c88fffcd-b6f8-0000-8fec-00b5497e2117");
            var paths = ObjectMoveSnapshot.DiffObjectXml(before, after, "Alert");
            Assert.Empty(paths);
        }

        [Fact]
        public void DiffObjectXml_AuthoredElementChange_StillFails()
        {
            string after = PropertyEchoAfterFolderMove.Replace("Name[1]=TempProc", "Name[1]=Renamed");
            var paths = ObjectMoveSnapshot.DiffObjectXml(PropertyEchoBefore, after, "operacional");
            Assert.Contains("Object[1]/Name[1]", paths);
        }

        [Fact]
        public void DiffObjectXml_AuthoredPropertyEchoingDestination_StillFails()
        {
            string after = PropertyEchoBefore.Replace("Value[1]=Keep", "Value[1]=operacional");
            var paths = ObjectMoveSnapshot.DiffObjectXml(PropertyEchoBefore, after, "operacional");
            Assert.Contains("Object[1]/Properties[1]/Property[1]/Value[1]", paths);
        }

        [Fact]
        public void FindCanonicalDifferencePaths_MatchesDiffObjectXmlWithoutPlacement()
        {
            Assert.Equal(
                ObjectMoveSnapshot.DiffObjectXml(PropertyEchoBefore, PropertyEchoAfterFolderMove, null),
                ObjectMoveSnapshot.FindCanonicalDifferencePaths(PropertyEchoBefore, PropertyEchoAfterFolderMove));
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
