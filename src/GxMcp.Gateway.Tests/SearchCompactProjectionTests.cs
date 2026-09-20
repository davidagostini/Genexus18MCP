using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // genexus_search joins the compact-default tool set: its 50-item result pages
    // carry per-item type metadata (guid/length/decimals) a scanning agent rarely
    // needs. These tests pin the projection behavior end-to-end through the same
    // NormalizeToolPayloadForAxi path used in production responses.
    public class SearchCompactProjectionTests
    {
        private static JObject BuildResponse(JObject payload, string toolName)
        {
            var envelope = Program.BuildToolTextResponse(new JValue("1"), payload, isError: false, toolName: toolName);
            var text = envelope["result"]?["content"]?[0]?["text"]?.ToString();
            Assert.NotNull(text);
            return JObject.Parse(text!);
        }

        [Fact]
        public void Search_DefaultProjectsCompactFields()
        {
            var payload = new JObject
            {
                ["count"] = 1,
                ["total"] = 1,
                ["results"] = new JArray(new JObject
                {
                    ["guid"] = "abc-123",
                    ["name"] = "TrnCliente",
                    ["type"] = "Transaction",
                    ["description"] = "Clientes",
                    ["path"] = "Client",
                    ["length"] = 10,
                    ["decimals"] = 0,
                    ["lastUpdate"] = "2026-08-22"
                })
            };

            var projected = BuildResponse(payload, "genexus_search");
            var item = (JObject)((JArray)projected["results"]!)[0];

            Assert.Equal("TrnCliente", item["name"]?.ToString());
            Assert.Equal("Transaction", item["type"]?.ToString());
            Assert.Equal("2026-08-22", item["lastUpdate"]?.ToString());
            Assert.Null(item["guid"]);      // dropped by compact allowlist
            Assert.Null(item["length"]);    // dropped
            Assert.Null(item["decimals"]);  // dropped
        }

        [Fact]
        public void Search_ExplicitFieldsWinOverCompactDefault()
        {
            var payload = new JObject
            {
                ["results"] = new JArray(new JObject { ["name"] = "X", ["guid"] = "g", ["type"] = "Procedure" })
            };
            var args = new JObject { ["fields"] = new JArray("guid") };

            var envelope = Program.BuildToolTextResponse(new JValue("1"), payload, isError: false, toolName: "genexus_search", toolArgs: args);
            var text = envelope["result"]?["content"]?[0]?["text"]?.ToString();
            var item = (JObject)((JArray)JObject.Parse(text!)["results"]!)[0];

            Assert.NotNull(item["guid"]);
            Assert.Null(item["name"]); // not in explicit field list
        }

        [Fact]
        public void Search_ProjectionVerboseKeepsEverything()
        {
            var payload = new JObject
            {
                ["results"] = new JArray(new JObject { ["name"] = "X", ["guid"] = "g", ["length"] = 5 })
            };
            var args = new JObject { ["projection"] = "verbose" };

            var envelope = Program.BuildToolTextResponse(new JValue("1"), payload, isError: false, toolName: "genexus_search", toolArgs: args);
            var text = envelope["result"]?["content"]?[0]?["text"]?.ToString();
            var item = (JObject)((JArray)JObject.Parse(text!)["results"]!)[0];

            Assert.NotNull(item["guid"]);
            Assert.NotNull(item["length"]);
        }
    }
}
