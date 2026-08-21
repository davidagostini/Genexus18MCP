using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ApiOpenApiServiceTests
    {
        [Fact]
        public void ExportOpenApi_ConvertsEndpointsToValidOpenApi3()
        {
            var endpoints = new List<ApiIntrospectService.HttpEndpoint>
            {
                new ApiIntrospectService.HttpEndpoint
                {
                    Name = "GetCustomerById",
                    HttpMethod = "GET",
                    Path = "/api/customers/{id}",
                    Parms = new List<ApiIntrospectService.Parm>
                    {
                        new ApiIntrospectService.Parm
                        {
                            Name = "id",
                            Direction = "in",
                            Type = "Numeric"
                        },
                        new ApiIntrospectService.Parm
                        {
                            Name = "CustomerData",
                            Direction = "out",
                            Type = "SDT:CustomerSDT"
                        }
                    }
                },
                new ApiIntrospectService.HttpEndpoint
                {
                    Name = "CreateCustomer",
                    HttpMethod = "POST",
                    Path = "/api/customers",
                    Parms = new List<ApiIntrospectService.Parm>
                    {
                        new ApiIntrospectService.Parm
                        {
                            Name = "CustomerInput",
                            Direction = "in",
                            Type = "SDT:CustomerSDT"
                        },
                        new ApiIntrospectService.Parm
                        {
                            Name = "Success",
                            Direction = "out",
                            Type = "Boolean"
                        }
                    }
                }
            };

            var spec = ApiOpenApiService.ExportOpenApi(endpoints, "GeneXus REST API", "1.0.0");

            Assert.NotNull(spec);
            Assert.Equal("3.0.3", spec["openapi"]?.ToString());
            Assert.Equal("GeneXus REST API", spec["info"]?["title"]?.ToString());

            var paths = spec["paths"] as JObject;
            Assert.NotNull(paths);
            Assert.NotNull(paths["/api/customers/{id}"]);
            Assert.NotNull(paths["/api/customers/{id}"]?["get"]);
            Assert.NotNull(paths["/api/customers"]?["post"]);
            Assert.NotNull(paths["/api/customers"]?["post"]?["requestBody"]);
        }

        [Fact]
        public void ImportOpenApi_ParsesOpenApi3IntoGeneXusBlueprint()
        {
            var rawJson = @"{
                ""openapi"": ""3.0.3"",
                ""info"": { ""title"": ""PetStore API"", ""version"": ""1.0.0"" },
                ""paths"": {
                    ""/pets"": {
                        ""get"": {
                            ""operationId"": ""listPets"",
                            ""summary"": ""List all pets"",
                            ""parameters"": [
                                { ""name"": ""limit"", ""in"": ""query"", ""required"": false, ""schema"": { ""type"": ""integer"" } }
                            ],
                            ""responses"": {
                                ""200"": {
                                    ""description"": ""A paged array of pets"",
                                    ""content"": {
                                        ""application/json"": {
                                            ""schema"": { ""$ref"": ""#/components/schemas/Pets"" }
                                        }
                                    }
                                }
                            }
                        },
                        ""post"": {
                            ""operationId"": ""addPet"",
                            ""summary"": ""Add a pet"",
                            ""requestBody"": {
                                ""content"": {
                                    ""application/json"": {
                                        ""schema"": { ""$ref"": ""#/components/schemas/Pet"" }
                                    }
                                }
                            }
                        }
                    }
                },
                ""components"": {
                    ""schemas"": {
                        ""Pet"": {
                            ""type"": ""object"",
                            ""required"": [""id"", ""name""],
                            ""properties"": {
                                ""id"": { ""type"": ""integer"", ""format"": ""int64"" },
                                ""name"": { ""type"": ""string"" },
                                ""address"": { ""$ref"": ""#/components/schemas/Address"" }
                            }
                        },
                        ""Pets"": {
                            ""type"": ""array"",
                            ""items"": { ""$ref"": ""#/components/schemas/Pet"" }
                        },
                        ""Address"": {
                            ""type"": ""object"",
                            ""properties"": {
                                ""street"": { ""type"": ""string"" }
                            }
                        }
                    }
                }
            }";

            var blueprint = ApiOpenApiService.ImportOpenApi(rawJson);

            Assert.NotNull(blueprint);
            Assert.True(blueprint.Success);
            Assert.Equal("PetStore API", blueprint.ApiTitle);

            // Schemas -> SDTs
            Assert.Contains(blueprint.SdtsToCreate, s => s.Name == "Pet" && s.Fields.Count == 3);
            var petSdt = blueprint.SdtsToCreate.Find(s => s.Name == "Pet");
            Assert.Contains(petSdt.Fields, f => f.Name == "address" && f.Type == "SDT:Address");

            Assert.Contains(blueprint.SdtsToCreate, s => s.Name == "Pets" && s.IsCollection);

            // Paths -> Services / Procedures
            Assert.Equal(2, blueprint.EndpointsToCreate.Count);
            var postEp = blueprint.EndpointsToCreate.Find(e => e.HttpMethod == "POST");
            Assert.NotNull(postEp);
            Assert.Equal("SDT:Pet", postEp.RequestBodyType);
        }
    }
}
