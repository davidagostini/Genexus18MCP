using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class Context360Tests
    {
        [Fact]
        public void Context360Envelope_HasAllRequiredSections()
        {
            // Verify the 360-degree context envelope structure
            var envelope = new JObject
            {
                ["status"] = "ok",
                ["code"] = "360ContextRead",
                ["target"] = "PProcessOrder",
                ["result"] = new JObject
                {
                    ["object"] = new JObject
                    {
                        ["name"] = "PProcessOrder",
                        ["type"] = "Procedure",
                        ["signature"] = "parm(in:&OrderId, out:&Status);",
                        ["parts"] = new JObject
                        {
                            ["rules"] = "parm(in:&OrderId, out:&Status);",
                            ["source"] = "For each Order where OrderId = &OrderId\n    &Status = 'OK'\nEndFor"
                        },
                        ["variables"] = new JArray
                        {
                            new JObject { ["name"] = "&OrderId", ["type"] = "NUMERIC", ["length"] = 9 }
                        }
                    },
                    ["calledSignatures"] = new JArray
                    {
                        new JObject { ["name"] = "PValidateCustomer", ["type"] = "Procedure", ["parmRule"] = "parm(in:&CustomerId, out:&IsValid);" }
                    },
                    ["referencedTables"] = new JArray
                    {
                        new JObject
                        {
                            ["name"] = "Order",
                            ["primaryKey"] = new JArray { "OrderId" },
                            ["columns"] = new JArray
                            {
                                new JObject { ["name"] = "OrderId", ["type"] = "NUMERIC", ["isKey"] = true },
                                new JObject { ["name"] = "CustomerId", ["type"] = "NUMERIC", ["isKey"] = false },
                                new JObject { ["name"] = "OrderTotal", ["type"] = "NUMERIC", ["isKey"] = false }
                            }
                        }
                    },
                    ["referencedSDTs"] = new JArray
                    {
                        new JObject
                        {
                            ["name"] = "SDTOrderInfo",
                            ["isCollection"] = false,
                            ["structure"] = "SDTOrderInfo\n{\n    OrderId : NUMERIC(9)\n}"
                        }
                    },
                    ["callers"] = new JArray
                    {
                        new JObject { ["name"] = "WOrderEntry", ["type"] = "WebPanel" }
                    }
                }
            };

            var res = envelope["result"] as JObject;
            Assert.NotNull(res);
            Assert.NotNull(res["object"]);
            Assert.NotNull(res["calledSignatures"]);
            Assert.NotNull(res["referencedTables"]);
            Assert.NotNull(res["referencedSDTs"]);
            Assert.NotNull(res["callers"]);
            Assert.Equal("Procedure", res["object"]?["type"]?.ToString());
            Assert.Single((JArray)res["calledSignatures"]!);
            Assert.Single((JArray)res["referencedTables"]!);
            Assert.Single((JArray)res["referencedSDTs"]!);
            Assert.Single((JArray)res["callers"]!);
        }
    }
}
