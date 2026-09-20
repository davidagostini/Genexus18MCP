using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Contract guard for names handled before the normal router/worker path.
    /// Every RequestLoop dispatch name must be declared, be a legacy rewrite, or
    /// be explicitly removed. This keeps gateway-only routes in discovery without
    /// changing their runtime resolution.
    /// </summary>
    public class ToolDispatchInventoryParityTests
    {
        private static string FindUp(params string[] segments)
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                var candidate = Path.Combine(new[] { dir }.Concat(segments).ToArray());
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("Could not locate " + string.Join("/", segments));
        }

        private static HashSet<string> DeclaredNames()
        {
            var path = FindUp("src", "GxMcp.Gateway", "tool_definitions.json");
            return new HashSet<string>(
                JArray.Parse(File.ReadAllText(path))
                    .OfType<JObject>()
                    .Select(t => t["name"]?.ToString())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!),
                StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> RequestLoopDispatchNames()
        {
            var source = File.ReadAllText(FindUp("src", "GxMcp.Gateway", "Program.RequestLoop.cs"));
            return new HashSet<string>(
                Regex.Matches(source, "if \\(string\\.Equals\\(toolName, \\\"(genexus_[^\\\"]+)\\\"")
                    .Cast<Match>()
                    .Select(m => m.Groups[1].Value),
                StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("genexus_worker_pool")]
        [InlineData("genexus_sandbox")]
        [InlineData("genexus_kb_diff")]
        [InlineData("genexus_kb_import")]
        public void FourGatewayRoutesAreDeclared(string toolName)
        {
            Assert.Contains(toolName, DeclaredNames());
            Assert.False(McpRouter.TryRewriteLegacyTool(toolName, new JObject(), out _, out _));
            Assert.False(RemovedToolsRegistry.Map.ContainsKey(toolName));
        }

        [Fact]
        public void EveryRequestLoopDispatchNameHasAnInventoryClassification()
        {
            var declared = DeclaredNames();
            var unclassified = RequestLoopDispatchNames()
                .Where(name => !declared.Contains(name)
                    && !McpRouter.TryRewriteLegacyTool(name, new JObject(), out _, out _)
                    && !RemovedToolsRegistry.Map.ContainsKey(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.True(unclassified.Length == 0,
                "RequestLoop dispatch names missing from declared/legacy-alias/removed inventory: "
                + string.Join(", ", unclassified));
        }
    }
}
