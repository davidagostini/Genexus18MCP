using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    // issue #59 — pure persistence-verification logic. Normalization must never cause a
    // false mismatch (the SDK persists boolean-ish properties in canonical spellings that
    // differ from what an agent writes), and the structured NotPersisted envelope must
    // carry before/requested/persisted so the caller can see the effective diff.
    public class PersistenceVerifierTests
    {
        [Theory]
        [InlineData("Yes", "yes")]
        [InlineData("True", "yes")]
        [InlineData("Y", "yes")]
        [InlineData("1", "yes")]
        [InlineData("No", "no")]
        [InlineData("False", "no")]
        [InlineData("N", "no")]
        [InlineData("0", "no")]
        [InlineData("  Hello  ", "hello")]
        [InlineData("Compatible", "compatible")]
        public void NormalizeForCompare_CanonicalBooleanFamilies(string input, string expected)
        {
            Assert.Equal(expected, PersistenceVerifier.NormalizeForCompare(input));
        }

        [Fact]
        public void ValuesMatch_RequestedYesPersistedTrue_Matches()
        {
            // The exact issue #57 shape: agent writes Nullable=Yes, the SDK persists
            // IsNullableValue.ToString() = "True".
            Assert.True(PersistenceVerifier.ValuesMatch("Yes", "True", allowBooleanAliases: true));
        }

        [Fact]
        public void ValuesMatch_StringValuesDoNotCollapseBooleanWords()
        {
            // Domain enum/string values are not nullable flags: "Yes" and "True"
            // are distinct values and must not be treated as equivalent.
            Assert.False(PersistenceVerifier.ValuesMatch("Yes", "True"));
        }

        [Fact]
        public void ValuesMatch_CaseInsensitive()
        {
            Assert.True(PersistenceVerifier.ValuesMatch("Active", "active"));
        }

        [Fact]
        public void ValuesMatch_TrimsWhitespace()
        {
            Assert.True(PersistenceVerifier.ValuesMatch("  A  ", "a"));
        }

        [Fact]
        public void ValuesMatch_DifferentValues_DoNotMatch()
        {
            Assert.False(PersistenceVerifier.ValuesMatch("Yes", "No"));
            Assert.False(PersistenceVerifier.ValuesMatch("Active", "Inactive"));
        }

        [Fact]
        public void ValuesMatch_NullRequested_MatchesEmptyPersisted()
        {
            Assert.True(PersistenceVerifier.ValuesMatch(null, ""));
        }

        [Fact]
        public void BuildNotPersistedError_CarriesStructuredDiff()
        {
            var err = PersistenceVerifier.BuildNotPersistedError(
                code: "PropertyNotPersisted",
                target: "Customer",
                property: "Nullable",
                requestedValue: "Yes",
                previousValue: "No",
                persistedValue: "No");

            var jo = JObject.Parse(err);
            Assert.Equal("error", jo["status"]?.ToString());
            Assert.Equal("PropertyNotPersisted", jo["error"]?["code"]?.ToString());
            Assert.Equal("Customer", jo["target"]?.ToString());
            Assert.Equal("Nullable", jo["error"]?["property"]?.ToString());
            Assert.Equal("Yes", jo["error"]?["requestedValue"]?.ToString());
            Assert.Equal("No", jo["error"]?["previousValue"]?.ToString());
            Assert.Equal("No", jo["error"]?["persistedValue"]?.ToString());
            Assert.False(jo["error"]?["saved"]?.ToObject<bool>());
        }

        [Fact]
        public void BuildNotPersistedError_MessageMentionsRequestedAndPersisted()
        {
            var err = PersistenceVerifier.BuildNotPersistedError(
                code: "DomainUpdateNotPersisted",
                target: "UserStatus",
                property: "enumValues",
                requestedValue: "A",
                previousValue: "I",
                persistedValue: "I");
            var jo = JObject.Parse(err);
            string msg = jo["error"]?["message"]?.ToString() ?? string.Empty;
            Assert.Contains("'A'", msg);
            Assert.Contains("'I'", msg);
        }

        [Fact]
        public void AttachPersistedDiff_AddsBeforeRequestedPersistedToResult()
        {
            string ok = "{\"status\":\"ok\",\"code\":\"PropertyApplied\",\"result\":{\"property\":\"Nullable\",\"value\":\"Yes\"}}";
            string decorated = PersistenceVerifier.AttachPersistedDiff(ok, "No", "Yes", "True");
            var jo = JObject.Parse(decorated);
            Assert.Equal("No", jo["result"]?["before"]?.ToString());
            Assert.Equal("Yes", jo["result"]?["requested"]?.ToString());
            Assert.Equal("True", jo["result"]?["persisted"]?.ToString());
            Assert.True(jo["result"]?["persistedVerified"]?.ToObject<bool>());
        }

        [Fact]
        public void AttachPersistedDiff_UnparseableInput_ReturnsInput()
        {
            string garbage = "not-json";
            Assert.Equal(garbage, PersistenceVerifier.AttachPersistedDiff(garbage, "a", "b", "c"));
        }
    }
}
