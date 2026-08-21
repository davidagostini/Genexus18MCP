using System;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class LiveFeaturesIntegrationTests
    {
        [Fact]
        public void LiveTest_1_AutoFixEngine_DiagnosesRealCompilerErrors()
        {
            var rawLines = new List<string>
            {
                "[BUILD] Compiling Procedure 'ProcessInvoice'...",
                "src0005: Variable 'CustomerDiscount' not defined. (Procedure 'ProcessInvoice' Source, Line: 12, Char: 5)",
                "spc0030: Subroutine 'CalculateTaxes' not defined. (Procedure 'ProcessInvoice' Source, Line: 25, Char: 9)",
                "spc0038: Attribute 'InvoiceTotal' is not in the base table. (Procedure 'ProcessInvoice' Source, Line: 40, Char: 3)",
                "Build failed with 3 errors."
            };

            var fixes = ErrorDiagnoser.Diagnose(rawLines);

            Assert.NotNull(fixes);
            Assert.Equal(3, fixes.Count);

            // 1. Missing Variable (spc0005 -> genexus_variable)
            Assert.Contains(fixes, f => f.ErrorCode == "spc0005" && f.Tool == "genexus_variable");
            // 2. Missing Subroutine (spc0053/0030 -> genexus_edit)
            Assert.Contains(fixes, f => f.ErrorCode == "spc0053" && f.Tool == "genexus_edit");
            // 3. Attribute not in table (spc0038 -> genexus_structure)
            Assert.Contains(fixes, f => f.ErrorCode == "spc0038" && f.Tool == "genexus_structure");
        }

        [Fact]
        public void LiveTest_2_OpenApiEngine_ExportsAndImportsLiveSpec()
        {
            // Part A: Export
            string procName = "GetCustomerDetails";
            var endpoints = new List<ApiIntrospectService.HttpEndpoint>
            {
                new ApiIntrospectService.HttpEndpoint
                {
                    Name = procName,
                    HttpMethod = "GET",
                    Url = "/GetCustomerDetails",
                    Path = "/GetCustomerDetails",
                    Parms = new List<ApiIntrospectService.Parm>
                    {
                        new ApiIntrospectService.Parm { Name = "CustomerId", Direction = "in", Type = "Numeric(6.0)" },
                        new ApiIntrospectService.Parm { Name = "CustomerData", Direction = "out", Type = "SDT:CustomerDetails" }
                    }
                }
            };

            var openApiObj = ApiOpenApiService.ExportOpenApi(endpoints, "CustomerAPI", "1.0.0", "Customer Service");
            Assert.NotNull(openApiObj);
            Assert.Equal("3.0.3", openApiObj["openapi"]?.ToString());
            Assert.NotNull(openApiObj["paths"]?["/GetCustomerDetails"]);

            // Part B: Import
            string openApiJson = @"{
                ""openapi"": ""3.0.0"",
                ""info"": { ""title"": ""Payment API"", ""version"": ""1.0.0"" },
                ""paths"": {
                    ""/ProcessPayment"": {
                        ""post"": {
                            ""summary"": ""Process payment transaction"",
                            ""requestBody"": {
                                ""content"": {
                                    ""application/json"": {
                                        ""schema"": { ""$ref"": ""#/components/schemas/PaymentRequest"" }
                                    }
                                }
                            },
                            ""responses"": {
                                ""200"": {
                                    ""description"": ""Payment response"",
                                    ""content"": {
                                        ""application/json"": {
                                            ""schema"": { ""$ref"": ""#/components/schemas/PaymentResponse"" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }";

            var plan = ApiOpenApiService.ImportOpenApi(openApiJson);
            Assert.NotNull(plan);
            Assert.True(plan.Success);
            Assert.NotEmpty(plan.EndpointsToCreate);
            Assert.Single(plan.EndpointsToCreate);

            var ep = plan.EndpointsToCreate[0];
            Assert.Equal("POST_ProcessPayment", ep.OperationId);
            Assert.Equal("POST", ep.HttpMethod);
            Assert.Contains(ep.Parameters, p => p.Type == "SDT:PaymentRequest" && p.Direction == "in");
        }

        [Fact]
        public void LiveTest_3_DesignSystemEngine_TokensClassesAndSanitization()
        {
            string dsoContent = @"
styles MainDesignSystem {
    @tokens {
        #colors {
            primary: #0066CC;
            background: #F8F9FA;
            // { Note: brace inside comment should not break parser }
        }
        #spacing {
            padding-md: 16px;
        }
    }

    .Button {
        background-color: $colors.primary;
        padding: $spacing.padding-md;
        border-radius: 4px;
    }

    .Button:hover {
        background-color: #0052A3;
    }
}
";

            // Validate
            var valResult = DesignSystemService.ValidateDso(dsoContent);
            Assert.True(valResult.IsValid, $"DSO Validation failed: {string.Join("; ", valResult.Errors)}");

            // Extract Tokens
            var tokens = DesignSystemService.ParseDsoTokens(dsoContent);
            Assert.NotNull(tokens);
            Assert.True(tokens.ContainsKey("colors"));
            Assert.Equal("#0066CC", tokens["colors"]["primary"]?.ToString());

            // Extract Classes (including pseudo-classes)
            var classes = DesignSystemService.ParseDsoClasses(dsoContent);
            Assert.NotNull(classes);
            Assert.True(classes.ContainsKey("Button"));
            Assert.True(classes.ContainsKey("Button:hover"));
        }

        [Fact]
        public void LiveTest_4_GxTestGenerator_ProducesIdiomaticGeneXusSource()
        {
            var parms = new List<ApiIntrospectService.Parm>
            {
                new ApiIntrospectService.Parm { Name = "OrderId", Direction = "in", Type = "Numeric(8.0)" },
                new ApiIntrospectService.Parm { Name = "TotalAmount", Direction = "out", Type = "Numeric(10.2)" },
                new ApiIntrospectService.Parm { Name = "IsApproved", Direction = "out", Type = "Boolean" },
                new ApiIntrospectService.Parm { Name = "Messages", Direction = "out", Type = "SDT:Messages" }
            };

            var testPlan = GxTestGeneratorService.GenerateUnitTestCode("CalculateOrderTotal", parms, useGxTestAssertModule: false);

            Assert.NotNull(testPlan);
            Assert.Equal("Test_CalculateOrderTotal", testPlan.TestObjectName);
            Assert.Equal(4, testPlan.Variables.Count);

            // Verify order: entrypoint DO statements MUST precede Sub definitions
            int doIdx = testPlan.GeneratedSource.IndexOf("Do 'Test_HappyPath'");
            int subIdx = testPlan.GeneratedSource.IndexOf("Sub 'Test_HappyPath'");
            Assert.True(doIdx >= 0 && subIdx >= 0 && doIdx < subIdx);

            // Verify universal assertions
            Assert.Contains("CalculateOrderTotal.Call(&OrderId, &TotalAmount, &IsApproved, &Messages)", testPlan.GeneratedSource);
            Assert.Contains("if not (&IsApproved)", testPlan.GeneratedSource);
            Assert.Contains("if (&Messages.Count > 0)", testPlan.GeneratedSource);
            Assert.Contains("if not (&TotalAmount >= 0)", testPlan.GeneratedSource);
        }
    }
}
