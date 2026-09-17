using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public sealed class ModuleServiceTests
    {
        [Fact]
        public void Package_action_is_recognized_before_kb_resolution()
        {
            JObject response = JObject.Parse(new ModuleService(null, null).Run(new JObject
            {
                ["action"] = "package",
                ["name"] = "Sample",
                ["outputPath"] = "C:\\temp"
            }));

            Assert.NotEqual("BadAction", response["error"]?["code"]?.ToString());
            Assert.Equal("NoKbOpen", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Unknown_action_returns_a_structured_error()
        {
            JObject response = JObject.Parse(new ModuleService(null, null).Run(new JObject
            {
                ["action"] = "not-a-module-action"
            }));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("BadAction", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Stable_module_key_prefers_sdk_identity_and_preserves_homonyms()
        {
            Assert.Equal(
                "guid:abc",
                ModuleService.BuildStableModuleKey("ABC", "entity-1", "One/Shared", "Shared"));
            Assert.Equal(
                "entity:entity-1",
                ModuleService.BuildStableModuleKey(null, "Entity-1", "One/Shared", "Shared"));
            Assert.NotEqual(
                ModuleService.BuildStableModuleKey(null, null, "One/Shared", "Shared"),
                ModuleService.BuildStableModuleKey(null, null, "Two/Shared", "Shared"));
        }
    }
}
