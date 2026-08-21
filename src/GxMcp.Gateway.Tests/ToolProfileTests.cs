using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class ToolProfileTests
    {
        private static JArray CreateSampleTools()
        {
            var tools = new List<string>
            {
                "genexus_whoami",
                "genexus_query",
                "genexus_read",
                "genexus_edit",
                "genexus_create",
                "genexus_structure",
                "genexus_variable",
                "genexus_layout",
                "genexus_wwp",
                "genexus_db",
                "genexus_data_view",
                "genexus_lifecycle",
                "genexus_test",
                "genexus_gxserver",
                "genexus_deploy"
            };

            var arr = new JArray();
            foreach (var t in tools)
            {
                arr.Add(new JObject { ["name"] = t, ["description"] = $"Description for {t}" });
            }
            return arr;
        }

        [Theory]
        [InlineData(null, 15)]
        [InlineData("", 15)]
        [InlineData("all", 15)]
        [InlineData("ALL", 15)]
        public void Filter_AllOrNull_ReturnsAllTools(string? profile, int expectedCount)
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, profile);
            Assert.Equal(expectedCount, filtered.Count);
        }

        [Fact]
        public void Filter_CoreProfile_ReturnsOnlyCoreTools()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "core");

            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();
            Assert.Contains("genexus_whoami", names);
            Assert.Contains("genexus_query", names);
            Assert.Contains("genexus_read", names);
            Assert.Contains("genexus_edit", names);
            Assert.Contains("genexus_lifecycle", names);

            Assert.DoesNotContain("genexus_create", names);
            Assert.DoesNotContain("genexus_db", names);
            Assert.DoesNotContain("genexus_layout", names);
            Assert.DoesNotContain("genexus_gxserver", names);
        }

        [Fact]
        public void Filter_AuthoringProfile_IncludesCoreAndAuthoringTools()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "authoring");

            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();
            Assert.Contains("genexus_read", names);
            Assert.Contains("genexus_create", names);
            Assert.Contains("genexus_structure", names);
            Assert.Contains("genexus_variable", names);

            Assert.DoesNotContain("genexus_gxserver", names);
            Assert.DoesNotContain("genexus_deploy", names);
        }

        [Fact]
        public void Filter_DevOpsProfile_IncludesDevopsTools()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "devops");

            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();
            Assert.Contains("genexus_lifecycle", names);
            Assert.Contains("genexus_test", names);
            Assert.Contains("genexus_gxserver", names);
            Assert.Contains("genexus_deploy", names);

            Assert.DoesNotContain("genexus_create", names);
            Assert.DoesNotContain("genexus_layout", names);
        }

        [Fact]
        public void Filter_UIProfile_IncludesUITools()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "ui");

            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();
            Assert.Contains("genexus_read", names);
            Assert.Contains("genexus_layout", names);
            Assert.Contains("genexus_wwp", names);

            Assert.DoesNotContain("genexus_deploy", names);
            Assert.DoesNotContain("genexus_gxserver", names);
        }

        [Fact]
        public void Filter_DbProfile_IncludesDbTools()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "db");

            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();
            Assert.Contains("genexus_read", names);
            Assert.Contains("genexus_db", names);
            Assert.Contains("genexus_data_view", names);

            Assert.DoesNotContain("genexus_layout", names);
            Assert.DoesNotContain("genexus_wwp", names);
        }

        [Fact]
        public void Filter_CompositeProfile_UnionsToolsFromBothProfiles()
        {
            var tools = CreateSampleTools();
            var filtered = ToolProfileFilter.Filter(tools, "ui, db");

            var names = filtered.Select(t => t["name"]?.ToString()).ToHashSet();
            Assert.Contains("genexus_layout", names);
            Assert.Contains("genexus_db", names);
            Assert.Contains("genexus_data_view", names);
            Assert.Contains("genexus_wwp", names);

            Assert.DoesNotContain("genexus_deploy", names);
        }
    }
}
