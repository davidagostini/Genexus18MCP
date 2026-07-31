using System.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue 55 ground truth (2026-07-31, GeneXus 18.0.10): the SDK persists enum values
    // in the stored XML as RAW literals for every data family (the template's own
    // HttpMethod char enum stores <Value>GET</Value>, unquoted). Values pass verbatim.
    // The real silent-drop mechanism was duplicate descriptions (empty included):
    // EnumValuesValidResolver rejects such a set, and the property write no-ops.
    public class DomainEnumQuotingTests
    {
        [Fact]
        public void FromJson_PassesBareValuesVerbatim()
        {
            var arr = new JArray(
                new JObject { ["name"] = "Active", ["value"] = "A" },
                new JObject { ["name"] = "Blocked", ["value"] = "B" });

            var specs = DomainEnumValues.FromJson(arr);

            Assert.Equal(2, specs.Count);
            Assert.Equal("A", specs[0].Value);
            Assert.Equal("B", specs[1].Value);
        }

        [Fact]
        public void FromJson_PassesPreQuotedValuesVerbatim()
        {
            var arr = new JArray(new JObject { ["name"] = "Status", ["value"] = "\"R\"" });

            var specs = DomainEnumValues.FromJson(arr);

            Assert.Equal("\"R\"", Assert.Single(specs).Value);
        }

        [Fact]
        public void FromJson_KeepsDescription()
        {
            var arr = new JArray(
                new JObject { ["name"] = "A", ["value"] = "1", ["description"] = "alpha" });

            var spec = Assert.Single(DomainEnumValues.FromJson(arr));
            Assert.Equal("alpha", spec.Description);
        }

        [Fact]
        public void FromJson_DefaultsMissingDescriptionToName()
        {
            // ISSUE-55: EnumValuesValidResolver rejects a set where two values share a
            // description (empty included), silently dropping the whole enum write.
            // Name-defaulted descriptions are unique by construction.
            var arr = new JArray(
                new JObject { ["name"] = "Red", ["value"] = "R" },
                new JObject { ["name"] = "Blue", ["value"] = "B", ["description"] = "" });

            var specs = DomainEnumValues.FromJson(arr);

            Assert.Equal("Red", specs[0].Description);
            Assert.Equal("Blue", specs[1].Description);
        }

        [Fact]
        public void FromJson_SkipsItemsWithoutName()
        {
            var arr = new JArray(
                new JObject { ["value"] = "1" },
                new JObject { ["name"] = "Kept", ["value"] = "2" });

            var specs = DomainEnumValues.FromJson(arr);

            Assert.Equal("Kept", Assert.Single(specs).Name);
        }

        [Fact]
        public void FromJson_HandlesNullAndEmpty()
        {
            Assert.Empty(DomainEnumValues.FromJson(null));
            Assert.Empty(DomainEnumValues.FromJson(new JArray()));
        }
    }
}
