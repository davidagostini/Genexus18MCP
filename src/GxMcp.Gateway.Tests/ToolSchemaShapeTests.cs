using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Structural validators for tool_definitions.json. These catch the class of bug where a
    /// schema works on Claude/Anthropic (lenient parser) but breaks on Gemini/Vertex AI / any
    /// stricter OpenAPI-flavoured consumer.
    ///
    /// History: Gemini rejected v2.6.12 because genexus_run_object.args declared
    /// {"type":"array"} with no "items" field. The error surfaced as a 400 from
    /// GenerateContentRequest with "tools[N].function_declarations[0].parameters.properties[args].items: missing field".
    /// This file exists so that class of bug fails CI instead of a user's chat session.
    /// </summary>
    public class ToolSchemaShapeTests
    {
        private static string FindToolDefinitionsJson()
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "tool_definitions.json");
            if (File.Exists(beside)) return beside;
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string candidate = Path.Combine(dir, "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir, "src", "GxMcp.Gateway", "tool_definitions.json");
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("Could not locate tool_definitions.json from " + AppContext.BaseDirectory);
        }

        private static JsonDocument LoadTools()
        {
            var path = FindToolDefinitionsJson();
            return JsonDocument.Parse(File.ReadAllText(path));
        }

        /// <summary>
        /// Every property declared as type=array MUST carry a non-null "items" subschema.
        /// Gemini/Vertex AI rejects array-without-items with HTTP 400
        /// (function_declarations[].parameters.properties[X].items: missing field).
        /// </summary>
        [Fact]
        public void EveryArrayPropertyHasItems()
        {
            using var doc = LoadTools();
            var violations = new List<string>();

            foreach (var tool in doc.RootElement.EnumerateArray())
            {
                var toolName = tool.GetProperty("name").GetString() ?? "<unnamed>";
                if (!tool.TryGetProperty("inputSchema", out var schema)) continue;
                WalkForArrayMissingItems(schema, toolName + ".inputSchema", violations);
            }

            Assert.True(violations.Count == 0,
                "tool_definitions.json has type:array properties without items (Gemini/Vertex AI HTTP 400):\n  - " +
                string.Join("\n  - ", violations));
        }

        /// <summary>
        /// Every property declared with an "enum" must be a non-empty array. An empty enum
        /// produces a tool the agent cannot legally call (every value fails validation).
        /// </summary>
        [Fact]
        public void EveryEnumIsNonEmpty()
        {
            using var doc = LoadTools();
            var violations = new List<string>();

            foreach (var tool in doc.RootElement.EnumerateArray())
            {
                var toolName = tool.GetProperty("name").GetString() ?? "<unnamed>";
                if (!tool.TryGetProperty("inputSchema", out var schema)) continue;
                WalkForEmptyEnum(schema, toolName + ".inputSchema", violations);
            }

            Assert.True(violations.Count == 0,
                "tool_definitions.json has empty enum arrays:\n  - " + string.Join("\n  - ", violations));
        }

        /// <summary>
        /// Every name in a tool's top-level "required" array must exist in "properties".
        /// A required-but-undeclared property is undefined behavior and confuses agents
        /// (some refuse to call the tool at all).
        /// </summary>
        [Fact]
        public void RequiredFieldsExistInProperties()
        {
            using var doc = LoadTools();
            var violations = new List<string>();

            foreach (var tool in doc.RootElement.EnumerateArray())
            {
                var toolName = tool.GetProperty("name").GetString() ?? "<unnamed>";
                if (!tool.TryGetProperty("inputSchema", out var schema)) continue;
                WalkForRequiredMismatch(schema, toolName + ".inputSchema", violations);
            }

            Assert.True(violations.Count == 0,
                "tool_definitions.json has 'required' entries with no matching property:\n  - " +
                string.Join("\n  - ", violations));
        }

        /// <summary>
        /// Tool names must be unique. Duplicates silently shadow each other in tools/list
        /// and the dispatcher routes by name — a duplicate means dead code.
        /// </summary>
        [Fact]
        public void ToolNamesAreUnique()
        {
            using var doc = LoadTools();
            var names = doc.RootElement.EnumerateArray()
                .Select(t => t.GetProperty("name").GetString() ?? "")
                .ToList();
            var dupes = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dupes.Count == 0, "Duplicate tool names: " + string.Join(", ", dupes));
        }

        [Fact]
        public void GeneratorReferenceToolHasExactTypedActionsAndSelection()
        {
            using var doc = LoadTools();
            var tool = doc.RootElement.EnumerateArray()
                .Single(t => t.GetProperty("name").GetString() == "genexus_generator_reference");
            var schema = tool.GetProperty("inputSchema");
            var actions = schema.GetProperty("properties").GetProperty("action").GetProperty("enum")
                .EnumerateArray().Select(x => x.GetString()).ToArray();
            var required = schema.GetProperty("required").EnumerateArray()
                .Select(x => x.GetString()).ToArray();

            Assert.Equal(new[] { "list", "dry_run_add", "add", "dry_run_remove", "remove" }, actions);
            Assert.Equal(new[] { "action", "environment", "generator" }, required);
        }

        // ---- recursive walkers --------------------------------------------------------

        private static void WalkForArrayMissingItems(JsonElement node, string pointer, List<string> violations)
        {
            if (node.ValueKind != JsonValueKind.Object) return;

            if (node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "array")
            {
                if (!node.TryGetProperty("items", out var items) ||
                    items.ValueKind == JsonValueKind.Null ||
                    items.ValueKind == JsonValueKind.Undefined)
                {
                    violations.Add(pointer + " (type=array missing items)");
                }
            }

            if (node.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in props.EnumerateObject())
                    WalkForArrayMissingItems(p.Value, pointer + ".properties." + p.Name, violations);
            }
            if (node.TryGetProperty("items", out var itemsNode) && itemsNode.ValueKind == JsonValueKind.Object)
                WalkForArrayMissingItems(itemsNode, pointer + ".items", violations);
            foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
            {
                if (node.TryGetProperty(key, out var combo) && combo.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var sub in combo.EnumerateArray())
                    {
                        WalkForArrayMissingItems(sub, pointer + "." + key + "[" + i + "]", violations);
                        i++;
                    }
                }
            }
        }

        private static void WalkForEmptyEnum(JsonElement node, string pointer, List<string> violations)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            if (node.TryGetProperty("enum", out var en))
            {
                if (en.ValueKind != JsonValueKind.Array || en.GetArrayLength() == 0)
                    violations.Add(pointer + " (enum is empty or not an array)");
            }
            if (node.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in props.EnumerateObject())
                    WalkForEmptyEnum(p.Value, pointer + ".properties." + p.Name, violations);
            }
            if (node.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
                WalkForEmptyEnum(items, pointer + ".items", violations);
            foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
            {
                if (node.TryGetProperty(key, out var combo) && combo.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var sub in combo.EnumerateArray())
                    {
                        WalkForEmptyEnum(sub, pointer + "." + key + "[" + i + "]", violations);
                        i++;
                    }
                }
            }
        }

        private static void WalkForRequiredMismatch(JsonElement node, string pointer, List<string> violations)
        {
            if (node.ValueKind != JsonValueKind.Object) return;

            if (node.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                JsonElement props = default;
                bool hasProps = node.TryGetProperty("properties", out props) && props.ValueKind == JsonValueKind.Object;
                foreach (var name in req.EnumerateArray())
                {
                    if (name.ValueKind != JsonValueKind.String) continue;
                    var n = name.GetString();
                    if (n == null) continue;
                    if (!hasProps || !props.TryGetProperty(n, out _))
                        violations.Add(pointer + ".required[" + n + "] not declared in properties");
                }
            }

            if (node.TryGetProperty("properties", out var p2) && p2.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in p2.EnumerateObject())
                    WalkForRequiredMismatch(p.Value, pointer + ".properties." + p.Name, violations);
            }
            if (node.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
                WalkForRequiredMismatch(items, pointer + ".items", violations);
            foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
            {
                if (node.TryGetProperty(key, out var combo) && combo.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var sub in combo.EnumerateArray())
                    {
                        WalkForRequiredMismatch(sub, pointer + "." + key + "[" + i + "]", violations);
                        i++;
                    }
                }
            }
        }
    }
}
