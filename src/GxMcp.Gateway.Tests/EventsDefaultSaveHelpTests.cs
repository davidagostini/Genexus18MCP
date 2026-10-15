using Xunit;
using Newtonsoft.Json.Linq;
using GxMcp.TestSupport;

namespace GxMcp.Gateway.Tests
{
    public class EventsDefaultSaveHelpTests
    {
        [Fact]
        public void ConditionalVerificationDefaultIsNotOverriddenBySchema()
        {
            var tools = JArray.Parse(System.IO.File.ReadAllText(RepoSource.PathOf("src", "GxMcp.Gateway", "tool_definitions.json")));
            JObject edit = null!;
            foreach (JObject tool in tools)
                if (tool["name"]?.ToString() == "genexus_edit") edit = tool;
            Assert.NotNull(edit);
            var mode = edit["inputSchema"]!["properties"]!["verifyMode"]!;
            Assert.Null(mode["default"]);
            Assert.Contains("Structure defaults to semantic", mode["description"]!.ToString());
        }

        [Fact]
        public void DefaultSaveDocumentsVerificationLimitsAndSiblingReadback()
        {
            string help = ToolHelpCatalog.Get("genexus_edit")!;
            Assert.Contains("Without `requireObjectSave`", help);
            Assert.Contains("object-level Save", help);
            Assert.Contains("does not certify isolation", help);
            Assert.Contains("Re-read sibling parts and the pattern instance", help);
            Assert.Contains("where strict Events isolation is unavailable", help);
        }
    }
}
