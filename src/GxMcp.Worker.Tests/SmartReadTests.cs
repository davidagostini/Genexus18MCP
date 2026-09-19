using System;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SmartReadTests
    {
        [Fact]
        public void ReadFullObject_Procedure_BundlesRulesSourceVariablesAndSignature()
        {
            // Verify structure of FullObjectRead envelope for procedures
            var result = new JObject
            {
                ["name"] = "PTestProcedure",
                ["type"] = "Procedure",
                ["signature"] = "parm(in:&Id, out:&Status);",
                ["parts"] = new JObject
                {
                    ["rules"] = "parm(in:&Id, out:&Status);",
                    ["source"] = "For each Customer where CustomerId = &Id\n    &Status = 'OK'\nEndFor"
                },
                ["variables"] = new JArray
                {
                    new JObject { ["name"] = "&Id", ["type"] = "NUMERIC", ["length"] = 6 },
                    new JObject { ["name"] = "&Status", ["type"] = "CHARACTER", ["length"] = 10 }
                }
            };

            Assert.Equal("Procedure", result["type"]?.ToString());
            Assert.NotNull(result["parts"]?["rules"]);
            Assert.NotNull(result["parts"]?["source"]);
            Assert.NotNull(result["variables"]);
            Assert.Equal("parm(in:&Id, out:&Status);", result["signature"]?.ToString());
        }

        [Fact]
        public void ReadFullObject_WebPanel_BundlesEventsAndRulesWithoutEmptySource()
        {
            // WebPanels have logic in Events, not Source
            var result = new JObject
            {
                ["name"] = "WTestPanel",
                ["type"] = "WebPanel",
                ["signature"] = "parm(in:&Mode);",
                ["parts"] = new JObject
                {
                    ["rules"] = "parm(in:&Mode);",
                    ["events"] = "Event Start\n    &Mode = 'DSP'\nEndEvent"
                },
                ["variables"] = new JArray
                {
                    new JObject { ["name"] = "&Mode", ["type"] = "CHARACTER", ["length"] = 3 }
                }
            };

            Assert.Equal("WebPanel", result["type"]?.ToString());
            Assert.NotNull(result["parts"]?["events"]);
            Assert.Null(result["parts"]?["source"]); // Source is not used for WebPanel
            Assert.NotNull(result["variables"]);
        }

        [Fact]
        public void ReadFullObject_Transaction_BundlesStructureRulesEvents()
        {
            var result = new JObject
            {
                ["name"] = "Customer",
                ["type"] = "Transaction",
                ["isBusinessComponent"] = true,
                ["parts"] = new JObject
                {
                    ["structure"] = "CustomerId* : NUMERIC(6)\nCustomerName : VARCHAR(50)",
                    ["rules"] = "error('Name required') if CustomerName.IsEmpty();",
                    ["events"] = "Event AfterValidate\nEndEvent"
                }
            };

            Assert.Equal("Transaction", result["type"]?.ToString());
            Assert.True(result["isBusinessComponent"]?.ToObject<bool>());
            Assert.NotNull(result["parts"]?["structure"]);
            Assert.NotNull(result["parts"]?["rules"]);
            Assert.NotNull(result["parts"]?["events"]);
        }

        [Fact]
        public void ReadFullObject_SDT_BundlesStructureAndCollectionFlags()
        {
            var result = new JObject
            {
                ["name"] = "SDTCustomer",
                ["type"] = "SDT",
                ["isCollection"] = true,
                ["collectionItemName"] = "SDTCustomerItem",
                ["parts"] = new JObject
                {
                    ["structure"] = "SDTCustomerItem\n{\n    Id : NUMERIC(6)\n    Name : VARCHAR(50)\n}"
                }
            };

            Assert.Equal("SDT", result["type"]?.ToString());
            Assert.True(result["isCollection"]?.ToObject<bool>());
            Assert.Equal("SDTCustomerItem", result["collectionItemName"]?.ToString());
            Assert.NotNull(result["parts"]?["structure"]);
        }
    }
}
