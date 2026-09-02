using System;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Pure-function helper tests for genexus_api. Exercise the regex parser,
    // schema projection, type mapping, and diff core without needing a live
    // KB — InternalsVisibleTo lets the test class see the internal static
    // helpers directly. The Run() entry-point is already smoke-tested by
    // scripts/smoke-futures.ps1 in the LiveKb harness.
    public class ApiIntrospectServiceTests
    {
        // ---- ExtractParmDeclaration ----------------------------------------

        [Fact]
        public void ExtractParmDeclaration_SimpleParm_ReturnsInnerArgs()
        {
            string rules = "parm(in:&Id, out:&Name);";
            string inner = ApiIntrospectService.ExtractParmDeclaration(rules);
            Assert.Equal("in:&Id, out:&Name", inner);
        }

        [Fact]
        public void ExtractParmDeclaration_NoParm_ReturnsNull()
        {
            string rules = "Call Protocol: HTTP;";
            Assert.Null(ApiIntrospectService.ExtractParmDeclaration(rules));
        }

        [Fact]
        public void ExtractParmDeclaration_MultilineWithComments_StillMatches()
        {
            string rules = "// header\n  parm( in:&A , inout:&B , out:&C );\n";
            string inner = ApiIntrospectService.ExtractParmDeclaration(rules);
            Assert.NotNull(inner);
            Assert.Contains("&A", inner);
            Assert.Contains("&C", inner);
        }

        // ---- MapToJsonType -------------------------------------------------

        [Theory]
        [InlineData("Numeric(8.2)", "number")]
        [InlineData("Numeric(4)", "number")]
        [InlineData("Boolean", "boolean")]
        [InlineData("Date", "string")]
        [InlineData("DateTime", "string")]
        [InlineData("Character(50)", "string")]
        [InlineData("VarChar(100)", "string")]
        [InlineData("", "string")]
        [InlineData(null, "string")]
        public void MapToJsonType_KnownGxTypes_MapToJsonScalars(string gxType, string expected)
        {
            Assert.Equal(expected, ApiIntrospectService.MapToJsonType(gxType));
        }

        // ---- BuildRequestSchema / BuildResponseSchema ----------------------

        [Fact]
        public void BuildRequestSchema_InAndInOutParms_AppearInProperties()
        {
            var ep = new ApiIntrospectService.HttpEndpoint
            {
                Name = "GetAluno",
                Parms =
                {
                    new ApiIntrospectService.Parm { Name = "Id", Direction = "in", Type = "Numeric(8)" },
                    new ApiIntrospectService.Parm { Name = "Snapshot", Direction = "inout", Type = "Character(20)" },
                    new ApiIntrospectService.Parm { Name = "Name", Direction = "out", Type = "Character(50)" },
                }
            };
            var req = ApiIntrospectService.BuildRequestSchema(ep);
            var props = (JObject)req["properties"];
            var required = (JArray)req["required"];

            Assert.Equal("object", req["type"].ToString());
            Assert.NotNull(props["Id"]);
            Assert.NotNull(props["Snapshot"]);
            Assert.Null(props["Name"]);           // out-only excluded from input schema
            Assert.Equal(2, required.Count);
            Assert.Contains("Id", required.Values<string>());
            Assert.Equal("number", props["Id"]["type"].ToString());
            Assert.Equal("Numeric(8)", props["Id"]["genexusType"].ToString());
        }

        [Fact]
        public void BuildResponseSchema_OutAndInOutParms_AppearInProperties()
        {
            var ep = new ApiIntrospectService.HttpEndpoint
            {
                Name = "GetAluno",
                Parms =
                {
                    new ApiIntrospectService.Parm { Name = "Id", Direction = "in", Type = "Numeric(8)" },
                    new ApiIntrospectService.Parm { Name = "Snapshot", Direction = "inout", Type = "Character(20)" },
                    new ApiIntrospectService.Parm { Name = "Name", Direction = "out", Type = "Character(50)" },
                }
            };
            var resp = ApiIntrospectService.BuildResponseSchema(ep);
            var props = (JObject)resp["properties"];

            Assert.Equal("object", resp["type"].ToString());
            Assert.Null(props["Id"]);             // in-only excluded from output schema
            Assert.NotNull(props["Snapshot"]);
            Assert.NotNull(props["Name"]);
        }

        // ---- BuildEndpointFromRules ----------------------------------------

        [Fact]
        public void BuildEndpointFromRules_NormalPath_PopulatesParmsAndDefaults()
        {
            string parm = "in:&Id, out:&Name";
            string rules = "parm(in:&Id, out:&Name);\nCall Protocol: HTTP;";
            var ep = ApiIntrospectService.BuildEndpointFromRules(
                name: "GetAluno",
                parmRule: parm,
                rulesSource: rules,
                path: "Root Module/API",
                lastUpdate: new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal("GetAluno", ep.Name);
            Assert.Equal("HTTP", ep.Protocol);
            Assert.Equal("REST", ep.CallMode);
            Assert.Equal("POST", ep.HttpMethod);   // default until an HttpMethod rule overrides
            Assert.Equal(2, ep.Parms.Count);
            Assert.Equal("in", ep.Parms[0].Direction);
            Assert.Equal("Id", ep.Parms[0].Name);
            Assert.Equal("Name", ep.Parms[1].Name);
        }

        // ---- DiffEndpoints -------------------------------------------------

        [Fact]
        public void DiffEndpoints_AddedAndRemoved_AppearInRespectiveBuckets()
        {
            var baseline = new JArray
            {
                new JObject { ["name"] = "GetA", ["httpMethod"] = "POST", ["parms"] = new JArray() },
                new JObject { ["name"] = "GetB", ["httpMethod"] = "POST", ["parms"] = new JArray() },
            };
            var current = new JArray
            {
                new JObject { ["name"] = "GetA", ["httpMethod"] = "POST", ["parms"] = new JArray() },
                new JObject { ["name"] = "GetC", ["httpMethod"] = "GET",  ["parms"] = new JArray() },
            };

            var diff = ApiIntrospectService.DiffEndpoints(baseline, current);
            var added = (JArray)diff["added"];
            var removed = (JArray)diff["removed"];

            Assert.Single(added);
            Assert.Equal("GetC", added[0]["name"].ToString());
            Assert.Single(removed);
            Assert.Equal("GetB", removed[0]["name"].ToString());
        }

        [Fact]
        public void DiffEndpoints_HttpMethodChanged_FlaggedAsBreaking()
        {
            var baseline = new JArray
            {
                new JObject { ["name"] = "GetA", ["httpMethod"] = "POST", ["parms"] = new JArray() },
            };
            var current = new JArray
            {
                new JObject { ["name"] = "GetA", ["httpMethod"] = "GET",  ["parms"] = new JArray() },
            };

            var diff = ApiIntrospectService.DiffEndpoints(baseline, current);
            var changed = (JArray)diff["changed"];
            Assert.Single(changed);
            var entry = (JObject)changed[0];
            var breaks = (JArray)entry["breaking"];
            Assert.Contains(breaks, b => b.ToString().StartsWith("httpMethod changed", System.StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void DiffEndpoints_ParamRemoved_FlaggedAsBreaking()
        {
            var baseline = new JArray
            {
                new JObject
                {
                    ["name"] = "GetA",
                    ["httpMethod"] = "POST",
                    ["parms"] = new JArray
                    {
                        new JObject { ["name"] = "Id", ["direction"] = "in", ["type"] = "Numeric(8)" },
                        new JObject { ["name"] = "Filter", ["direction"] = "in", ["type"] = "Character(20)" },
                    }
                }
            };
            var current = new JArray
            {
                new JObject
                {
                    ["name"] = "GetA",
                    ["httpMethod"] = "POST",
                    ["parms"] = new JArray
                    {
                        new JObject { ["name"] = "Id", ["direction"] = "in", ["type"] = "Numeric(8)" },
                    }
                }
            };

            var diff = ApiIntrospectService.DiffEndpoints(baseline, current);
            var changed = (JArray)diff["changed"];
            Assert.Single(changed);
            var breaks = (JArray)changed[0]["breaking"];
            Assert.Contains(breaks, b => b.ToString().Contains("param removed"));
        }

        [Fact]
        public void DiffEndpoints_ParamAdded_FlaggedAsCompat()
        {
            var baseline = new JArray
            {
                new JObject
                {
                    ["name"] = "GetA",
                    ["httpMethod"] = "POST",
                    ["parms"] = new JArray
                    {
                        new JObject { ["name"] = "Id", ["direction"] = "in", ["type"] = "Numeric(8)" },
                    }
                }
            };
            var current = new JArray
            {
                new JObject
                {
                    ["name"] = "GetA",
                    ["httpMethod"] = "POST",
                    ["parms"] = new JArray
                    {
                        new JObject { ["name"] = "Id", ["direction"] = "in", ["type"] = "Numeric(8)" },
                        new JObject { ["name"] = "Filter", ["direction"] = "in", ["type"] = "Character(20)" },
                    }
                }
            };

            var diff = ApiIntrospectService.DiffEndpoints(baseline, current);
            var changed = (JArray)diff["changed"];
            Assert.Single(changed);
            var compat = (JArray)changed[0]["compat"];
            Assert.Contains(compat, c => c.ToString().Contains("param added"));
        }

        [Fact]
        public void DiffEndpoints_NoChange_ReturnsEmptyChanged()
        {
            var unchanged = new JArray
            {
                new JObject
                {
                    ["name"] = "GetA",
                    ["httpMethod"] = "POST",
                    ["parms"] = new JArray { new JObject { ["name"] = "Id", ["direction"] = "in", ["type"] = "Numeric(8)" } }
                }
            };
            var diff = ApiIntrospectService.DiffEndpoints(unchanged, unchanged);
            Assert.Empty((JArray)diff["added"]);
            Assert.Empty((JArray)diff["removed"]);
            Assert.Empty((JArray)diff["changed"]);
        }

        // ---- Run() entrypoint shape ---------------------------------------

        [Fact]
        public void Run_MissingAction_DefaultsToList_ContentFirst()
        {
            // Content-first: a bare genexus_api call enumerates APIs instead of
            // erroring. With no index wired the list is empty, but the envelope
            // is a successful `list`, not an InvalidAction error.
            var svc = new ApiIntrospectService(null, null, null);
            var json = svc.Run(new JObject());
            var obj = JObject.Parse(json);
            Assert.Equal("ok", obj["status"]?.ToString());
            Assert.NotNull(obj["result"]?["endpoints"]);
        }

        [Fact]
        public void Run_UnknownAction_ReturnsInvalidActionEnvelope()
        {
            var svc = new ApiIntrospectService(null, null, null);
            var json = svc.Run(new JObject { ["action"] = "bogus" });
            var obj = JObject.Parse(json);
            Assert.Equal("error", obj["status"]?.ToString());
        }

        // ---- native API route plans ---------------------------------------

        [Fact]
        public void ApiRoutePlan_CloneChangesOnlyMethodAndPath()
        {
            const string source = @"api OrdersApi {
[RestMethod(POST), RestPath(""/orders/inbound/"")] inbound_post(in:&requestBody,out:&Retorno) => operational.OrderFlow(&requestBody, &Retorno);
[RestMethod(GET), RestPath(""/orders/inbound/&codOrder"")] inbound_get(in:&codOrder,out:&requestBody) => operational.OrderFlow(&codOrder, &requestBody);
}";

            var routes = ApiIntrospectService.ParseApiRoutes(source);
            Assert.Equal(2, routes.Count);
            Assert.Equal("POST", routes[0].Verb);
            Assert.Equal("/orders/inbound/", routes[0].Path);

            var plan = ApiIntrospectService.BuildApiRoutePlan(source, "inbound", "reverse", false);
            Assert.Empty(plan.Conflicts);
            Assert.Equal(new[] { "/orders/reverse/", "/orders/reverse/&codOrder" }, plan.Added.Select(r => r.Path));
            Assert.Contains("reverse_post", plan.CandidateSource);
            Assert.Contains("/orders/reverse/", plan.CandidateSource);
            Assert.Contains("operational.OrderFlow(&requestBody, &Retorno)", plan.CandidateSource);
            Assert.Contains("inbound_post", plan.CandidateSource);
            Assert.DoesNotContain("OrderReverse", plan.CandidateSource);
        }

        [Fact]
        public void ApiRoutePlan_CloneRejectsDifferentExistingTarget()
        {
            const string source = @"api OrdersApi {
[RestMethod(POST), RestPath(""/orders/inbound/"")] inbound_post(in:&Request) => operational.OrderInbound(&Request);
[RestMethod(POST), RestPath(""/orders/reverse/"")] reverse_post(in:&OtherRequest) => operational.Other(&OtherRequest);
}";

            var plan = ApiIntrospectService.BuildApiRoutePlan(source, "inbound", "reverse", false);
            Assert.Single(plan.Conflicts);
            Assert.Equal(source, plan.CandidateSource);
        }
    }
}
