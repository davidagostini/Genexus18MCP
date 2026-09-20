using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway;

/// <summary>
/// Preserves request shapes that were already published by an earlier
/// operational baseline when a newer catalog accidentally narrows them.
/// Keep every exception explicit and covered by a regression test.
/// </summary>
internal static class ToolSchemaCompatibility
{
    public static void Apply(JArray definitions)
    {
        var create = definitions.OfType<JObject>()
            .FirstOrDefault(tool => string.Equals(
                tool["name"]?.ToString(),
                "genexus_create",
                StringComparison.Ordinal));
        var required = create?["inputSchema"]?["properties"]?["variables"]?["items"]?["required"] as JArray;
        if (required is null)
        {
            return;
        }

        // The Worker accepts both `name` and `varName` for object_atomic variables.
        // v2.37 advertised both as optional; requiring only `varName` in v2.38
        // would reject previously valid callers before they reached the Worker.
        foreach (var token in required
                     .Where(token => string.Equals(token?.ToString(), "varName", StringComparison.Ordinal))
                     .ToArray())
        {
            token.Remove();
        }

        if (required.Count == 0)
        {
            (required.Parent as JProperty)?.Remove();
        }
    }
}
