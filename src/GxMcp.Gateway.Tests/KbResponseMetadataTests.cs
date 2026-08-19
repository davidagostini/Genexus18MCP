using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class KbResponseMetadataTests
    {
        [Fact]
        public void Adds_alias_to_object_without_mutating_original()
        {
            var original = new JObject
            {
                ["status"] = "ok",
                ["results"] = new JArray(new JObject { ["name"] = "Customer" })
            };

            var tagged = (JObject)Program.AddKbContextMetadata(original, " customer ");

            Assert.Equal("customer", tagged["kbAlias"]?.ToString());
            Assert.Null(original["kbAlias"]);
            Assert.Equal("Customer", tagged["results"]?[0]?["name"]?.ToString());
        }

        [Fact]
        public void Adds_alias_in_place_when_payload_is_owned()
        {
            var original = new JObject
            {
                ["status"] = "ok",
                ["results"] = new JArray(new JObject { ["name"] = "Customer" })
            };

            var tagged = (JObject)Program.AttachKbContextMetadataToOwnedPayload(original, " customer ");

            Assert.Same(original, tagged);
            Assert.Equal("customer", tagged["kbAlias"]?.ToString());
            Assert.Equal("Customer", tagged["results"]?[0]?["name"]?.ToString());
        }

        [Fact]
        public void Owned_attachment_keeps_defensive_clone_for_parented_payload()
        {
            var original = new JObject { ["status"] = "ok" };
            var envelope = new JObject { ["result"] = original };

            var tagged = (JObject)Program.AttachKbContextMetadataToOwnedPayload(original, "customer");

            Assert.NotSame(original, tagged);
            Assert.Null(original["kbAlias"]);
            Assert.Same(original, envelope["result"]);
            Assert.Equal("customer", tagged["kbAlias"]?.ToString());
        }

        [Fact]
        public void Wraps_array_as_results_with_alias()
        {
            var original = new JArray("one", "two");

            var tagged = (JObject)Program.AddKbContextMetadata(original, "orders");

            Assert.Equal("orders", tagged["kbAlias"]?.ToString());
            var results = Assert.IsType<JArray>(tagged["results"]);
            Assert.Equal(2, results.Count);
            Assert.Equal("one", results[0]?.ToString());
            Assert.Null(original.Parent);
        }

        [Fact]
        public void Wraps_scalar_as_value_with_alias()
        {
            var tagged = (JObject)Program.AddKbContextMetadata(JValue.CreateString("done"), "catalog");

            Assert.Equal("catalog", tagged["kbAlias"]?.ToString());
            Assert.Equal("done", tagged["value"]?.ToString());
        }
    }
}
