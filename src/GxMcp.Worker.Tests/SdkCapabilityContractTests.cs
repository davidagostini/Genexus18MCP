using System;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SdkCapabilityContractTests
    {
        [Theory]
        [InlineData("genexus_api", "10.3", "dotnet-reflection", false, "17")]
        [InlineData("genexus_api", "15", "dotnet-reflection", false, "17")]
        [InlineData("genexus_api", "16", "native-sdk", false, "17")]
        [InlineData("genexus_api", "17", "native-sdk", true, null)]
        [InlineData("genexus_api", "18", "native-sdk", true, null)]
        [InlineData("genexus_gam", "10.1", "dotnet-reflection", false, "10.2")]
        [InlineData("genexus_gam", "10.2", "dotnet-reflection", true, null)]
        [InlineData("genexus_gam", "10.3", "dotnet-reflection", true, null)]
        [InlineData("genexus_module", "10.1", "dotnet-reflection", false, "10.3")]
        [InlineData("genexus_module", "10.2", "dotnet-reflection", false, "10.3")]
        [InlineData("genexus_module", "10.3", "dotnet-reflection", true, null)]
        [InlineData("genexus_design_system", "15", "dotnet-reflection", false, "17")]
        [InlineData("genexus_generator_reference", "10.3", "dotnet-reflection", false, "17")]
        public void DynamicSdkBridge_EvaluatesToolCapabilitiesCorrectly(
            string toolName,
            string major,
            string driver,
            bool expectedSupported,
            string expectedMinMajor)
        {
            using (DynamicSdkBridge.Scoped(driver, major))
            {
                bool supported = DynamicSdkBridge.CheckCapability(null, toolName, "targetObj", out string errorJson);
                Assert.Equal(expectedSupported, supported);

                if (!expectedSupported)
                {
                    Assert.NotNull(errorJson);
                    var resp = JObject.Parse(errorJson);
                    Assert.Equal("error", resp["status"]?.ToString());
                    Assert.Equal("UNSUPPORTED_IN_GENEXUS_VERSION", resp["error"]?["code"]?.ToString());
                    Assert.Equal(expectedMinMajor, resp["error"]?["minSupportedMajor"]?.ToString());
                    Assert.Equal(major, resp["error"]?["currentMajor"]?.ToString());
                    Assert.Equal(driver, resp["error"]?["driver"]?.ToString());
                }
                else
                {
                    Assert.Null(errorJson);
                }
            }
        }

        [Theory]
        [InlineData("genexus_api")]
        [InlineData("genexus_gam")]
        [InlineData("genexus_module")]
        [InlineData("genexus_design_system")]
        [InlineData("genexus_apply_pattern")]
        [InlineData("genexus_wwp")]
        public void DynamicSdkBridge_RejectsModernToolsOnComDriver(string toolName)
        {
            using (DynamicSdkBridge.Scoped("com-gxpublic", "9"))
            {
                bool supported = DynamicSdkBridge.CheckCapability(null, toolName, "targetObj", out string errorJson);
                Assert.False(supported);
                var resp = JObject.Parse(errorJson);
                Assert.Equal("UNSUPPORTED_IN_GENEXUS_VERSION", resp["error"]?["code"]?.ToString());
                Assert.Equal("com-gxpublic", resp["error"]?["driver"]?.ToString());
                Assert.Contains("GXPublic", resp["error"]?["message"]?.ToString());
            }
        }

        [Fact]
        public void DynamicSdkBridge_AllowsCoreToolsOnComDriver()
        {
            using (DynamicSdkBridge.Scoped("com-gxpublic", "9"))
            {
                Assert.True(DynamicSdkBridge.CheckCapability("read", "genexus_read", "Client", out _));
                Assert.True(DynamicSdkBridge.CheckCapability("list", "genexus_list_objects", null, out _));
                Assert.True(DynamicSdkBridge.CheckCapability("search", "genexus_query", null, out _));
            }
        }

        [Fact]
        public void CommandDispatcher_ReturnsUnsupportedEnvelopeForLegacyMismatch()
        {
            using (DynamicSdkBridge.Scoped("dotnet-reflection", "10.3"))
            {
                var request = new JObject
                {
                    ["method"] = "api",
                    ["action"] = "list",
                    ["tool"] = "genexus_api",
                    ["params"] = new JObject()
                };

                string responseJson = CommandDispatcher.Instance.Dispatch(request, request.ToString());
                Assert.NotNull(responseJson);

                var resp = JObject.Parse(responseJson);
                Assert.Equal("error", resp["status"]?.ToString());
                Assert.Equal("UNSUPPORTED_IN_GENEXUS_VERSION", resp["error"]?["code"]?.ToString());
                Assert.Equal("17", resp["error"]?["minSupportedMajor"]?.ToString());
            }
        }

        [Fact]
        public void OptionalSdkInvoker_GetPartDynamic_HandlesNullSafely()
        {
            var part = OptionalSdkInvoker.GetPartDynamic(null, Guid.NewGuid());
            Assert.Null(part);
        }

        [Fact]
        public void OptionalSdkInvoker_CreateQualifiedName_ReturnsValueWithoutThrowing()
        {
            var qname = OptionalSdkInvoker.CreateQualifiedName("Client");
            Assert.NotNull(qname);
        }

        [Fact]
        public void DynamicSdkBridge_AttachesPrescriptiveGuidanceInHints()
        {
            using (DynamicSdkBridge.Scoped("dotnet-reflection", "10.3"))
            {
                DynamicSdkBridge.CheckCapability(null, "genexus_api", "target", out string errorJson);
                var resp = JObject.Parse(errorJson);
                Assert.Contains("OpenAPI REST objects do not exist", resp["error"]?["hint"]?.ToString());
            }

            using (DynamicSdkBridge.Scoped("dotnet-reflection", "10.1"))
            {
                DynamicSdkBridge.CheckCapability(null, "genexus_module", "target", out string errorJson);
                var resp = JObject.Parse(errorJson);
                Assert.Contains("Modules do not exist", resp["error"]?["hint"]?.ToString());

                DynamicSdkBridge.CheckCapability(null, "genexus_gam", "target", out string gamJson);
                var gamResp = JObject.Parse(gamJson);
                Assert.Contains("GAM does not exist", gamResp["error"]?["hint"]?.ToString());
            }

            using (DynamicSdkBridge.Scoped("com-gxpublic", "9"))
            {
                DynamicSdkBridge.CheckCapability(null, "genexus_gam", "target", out string comJson);
                var comResp = JObject.Parse(comJson);
                Assert.Contains("Supported tools include", comResp["error"]?["hint"]?.ToString());
            }
        }

        [Fact]
        public void DynamicSdkBridge_ResolvesEffectiveEncodingCorrectly()
        {
            using (DynamicSdkBridge.Scoped("com-gxpublic", "9"))
            {
                var enc = DynamicSdkBridge.GetEffectiveEncoding();
                Assert.Equal(1252, enc.CodePage);

                string original = "Validação de endereço com acentuação: á, é, í, ó, ú, ç, ã";
                byte[] bytes = enc.GetBytes(original);
                string roundtrip = enc.GetString(bytes);
                Assert.Equal(original, roundtrip);
            }

            using (DynamicSdkBridge.Scoped("dotnet-reflection", "10.2"))
            {
                var enc = DynamicSdkBridge.GetEffectiveEncoding();
                Assert.Equal(1252, enc.CodePage);
            }

            using (DynamicSdkBridge.Scoped("native-sdk", "18"))
            {
                var enc = DynamicSdkBridge.GetEffectiveEncoding();
                Assert.Equal(65001, enc.CodePage);
            }
        }
    }
}
